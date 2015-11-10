﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Contralto.Memory;

namespace Contralto.CPU
{
    public partial class AltoCPU
    {
        /// <summary>
        /// EmulatorTask provides emulator (NOVA instruction set) specific operations.
        /// </summary>
        private class EmulatorTask : Task
        {
            public EmulatorTask(AltoCPU cpu) : base(cpu)
            {
                _taskType = TaskType.Emulator;

                // The Wakeup signal is always true for the Emulator task.
                _wakeup = true;
            }

            public override void BlockTask()
            {
                throw new InvalidOperationException("The emulator task cannot be blocked.");
            }

            public override void WakeupTask()
            {
                throw new InvalidOperationException("The emulator task is always in wakeup state.");
            }

            protected override ushort GetBusSource(int bs)
            {
                EmulatorBusSource ebs = (EmulatorBusSource)bs;

                switch (ebs)
                {
                    case EmulatorBusSource.ReadSLocation:
                        if (_rSelect != 0)
                        {
                            return _cpu._s[_cpu._rb][_rSelect];
                        }
                        else
                        {
                            // "...when reading data from the S registers onto the processor bus,
                            //  the RSELECT value 0 causes the current value of the M register to
                            //  appear on the bus..."
                            return _cpu._m;
                        }

                    case EmulatorBusSource.LoadSLocation:
                        _loadS = true;
                        return 0;       // TODO: technically this is an "undefined value," not zero.

                    default:
                        throw new InvalidOperationException(String.Format("Unhandled bus source {0}", bs));
                }
            }

            protected override void ExecuteSpecialFunction1(MicroInstruction instruction)
            {
                EmulatorF1 ef1 = (EmulatorF1)instruction.F1;
                switch (ef1)
                {
                    case EmulatorF1.RSNF:
                        // TODO: make configurable
                        // "...decoded by the Ethernet interface, which gates the host address wired on the
                        // backplane onto BUS[8-15].  BUS[0-7] is not driven and will therefore be -1.  If
                        // no Ethernet interface is present, BUS will be -1.
                        //
                        _busData &= (0xff00 | 0x42);
                        break;

                    case EmulatorF1.STARTF:
                        // Dispatch function to Ethernet I/O based on contents of AC0... (TBD: what are these?)
                        // For now do nothing, since we have no Ethernet implemented
                        //throw new NotImplementedException();
                        break;

                    case EmulatorF1.SWMODE:
                        throw new NotImplementedException();
                        break;

                    default:
                        throw new InvalidOperationException(String.Format("Unhandled emulator F1 {0}.", ef1));
                }
            }

            protected override void ExecuteSpecialFunction2Early(MicroInstruction instruction)
            {
                EmulatorF2 ef2 = (EmulatorF2)instruction.F2;
                switch (ef2)
                {
                    case EmulatorF2.ACSOURCE:
                        // Early: modify R select field:
                        // "...it replaces the two-low order bits of the R select field with
                        // the complement of the SrcAC field of IR, (IR[1-2] XOR 3), allowing the emulator
                        // to address its accumulators (which are assigned to R0-R3)."
                        _rSelect = (_rSelect & 0xfffc) | ((((uint)_cpu._ir & 0x6000) >> 13) ^ 3);
                        break;

                    case EmulatorF2.ACDEST:
                        // "...causes (IR[3-4] XOR 3) to be used as the low-order two bits of the RSELECT field.
                        // This address the accumulators from the destination field of the instruction.  The selected
                        // register may be loaded or read."
                        _rSelect = (_rSelect & 0xfffc) | ((((uint)_cpu._ir & 0x1800) >> 11) ^ 3);
                        break;

                    case EmulatorF2.LoadDNS:
                        //
                        // "...DNS also addresses R from (3-IR[3 - 4])..."
                        //
                        _rSelect = (_rSelect & 0xfffc) | ((((uint)_cpu._ir & 0x1800) >> 11) ^ 3);
                        break;

                }
            }

            protected override void ExecuteSpecialFunction2(MicroInstruction instruction)
            {
                EmulatorF2 ef2 = (EmulatorF2)instruction.F2;
                switch (ef2)
                {
                    case EmulatorF2.LoadIR:
                        // based on block diagram, this always comes from the bus
                        _cpu._ir = _busData;

                        // "IR<- also merges bus bits 0, 5, 6 and 7 into NEXT[6-9] which does a first level
                        // instruction dispatch."
                        // TODO: is this an AND or an OR operation?  (how is the "merge" done?)
                        // Assuming for now this is an OR operation like everything else that modifies NEXT.
                        _nextModifier = (ushort)(((_busData & 0x8000) >> 12) | ((_busData & 0x0700) >> 8));

                        // "IR<- clears SKIP"
                        _skip = 0;
                        break;

                    case EmulatorF2.IDISP:
                        // "The IDISP function (F2=15B) does a 16 way dispatch under control of a PROM and a
                        // multiplexer.  The values are tabulated below:
                        //   Conditions             ORed onto NEXT          Comment
                        //
                        //   if IR[0] = 1           3-IR[8-9]               complement of SH field of IR
                        //   elseif IR[1-2] = 0     IR[3-4]                 JMP, JSR, ISZ, DSZ              ; dispatch selects register
                        //   elseif IR[1-2] = 1     4                       LDA
                        //   elseif IR[1-2] = 2     5                       STA
                        //   elseif IR[4-7] = 0     1                       
                        //   elseif IR[4-7] = 1     0
                        //   elseif IR[4-7] = 6     16B                     CONVERT
                        //   elseif IR[4-7] = 16B   6
                        //   else                   IR[4-7]
                        // NB: as always, Xerox labels bits in the opposite order from modern convention;
                        // (bit 0 is the msb...)
                        if ((_cpu._ir & 0x8000) != 0)
                        {
                            _nextModifier = (ushort)(3 - ((_cpu._ir & 0xc0) >> 6));
                        }
                        else if ((_cpu._ir & 0x6000) == 0)
                        {
                            _nextModifier = (ushort)((_cpu._ir & 0x1800) >> 11);
                        }
                        else if ((_cpu._ir & 0x6000) == 0x2000)
                        {
                            _nextModifier = 4;
                        }
                        else if ((_cpu._ir & 0x6000) == 0x4000)
                        {
                            _nextModifier = 5;
                        }
                        else if ((_cpu._ir & 0x0f00) == 0)
                        {
                            _nextModifier = 1;
                        }
                        else if ((_cpu._ir & 0x0f00) == 0x0100)
                        {
                            _nextModifier = 0;
                        }
                        else if ((_cpu._ir & 0x0f00) == 0x0600)
                        {
                            _nextModifier = 0xe;
                        }
                        else if ((_cpu._ir & 0x0f00) == 0x0e00)
                        {
                            _nextModifier = 0x6;
                        }
                        else
                        {
                            _nextModifier = (ushort)((_cpu._ir & 0x0f00) >> 8);
                        }
                        break;

                    case EmulatorF2.ACSOURCE:
                        // Late:
                        // "...a dispatch is performed:
                        //   Conditions             ORed onto NEXT          Comment
                        //
                        //   if IR[0] = 1           3-IR[8-9]               complement of SH field of IR
                        //   if IR[1-2] = 3         IR[5]                   the Indirect bit of R
                        //   if IR[3-7] = 0         2                       CYCLE
                        //   if IR[3-7] = 1         5                       RAMTRAP
                        //   if IR[3-7] = 2         3                       NOPAR -- parameterless opcode group
                        //   if IR[3-7] = 3         6                       RAMTRAP
                        //   if IR[3-7] = 4         7                       RAMTRAP
                        //   if IR[3-7] = 11B       4                       JSRII
                        //   if IR[3-7] = 12B       4                       JSRIS
                        //   if IR[3-7] = 16B       1                       CONVERT
                        //   if IR[3-7] = 37B       17B                     ROMTRAP -- used by Swat, the debugger
                        //   else                   16B                     ROMTRAP

                        //
                        // NOTE: the above table from the Hardware Manual is incorrect (or at least incomplete / misleading).
                        // There is considerably more that goes into determining the dispatch, which is controlled by a 256x8
                        // PROM.  We just use the PROM rather than implementing the above logic (because it works.)
                        //

                        if ((_cpu._ir & 0x8000) != 0)
                        {
                            // 3-IR[8-9] (shift field of arithmetic instruction)
                            _nextModifier = (ushort)(3 - ((_cpu._ir & 0xc0) >> 6));
                        }
                        else
                        {
                            // Use the PROM.                            
                            _nextModifier = ControlROM.ACSourceROM[((_cpu._ir & 0x7f00) >> 8)];
                        }                       
                        break;

                    case EmulatorF2.ACDEST:
                        // Handled in early handler, nothing to do here.
                        break;

                    case EmulatorF2.BUSODD:
                        // "...merges BUS[15] into NEXT[9]."
                        // TODO: is this an AND or an OR?
                        _nextModifier |= (ushort)(_busData & 0x1);
                        break;

                    case EmulatorF2.MAGIC:
                        Shifter.SetMagic(true);
                        break;
                        
                    case EmulatorF2.LoadDNS:
                        // DNS<- does the following:
                        // - modifies the normal shift operations to perform Nova-style shifts (done here)
                        // - addresses R from 3-IR[3-4] (destination AC)  (see Early LoadDNS handler)
                        // - stores into R unless IR[12] is set (done here)
                        // - calculates Nova-style CARRY bit (done here)
                        // - sets the SKIP and CARRY flip-flops appropriately (see Late LoadDNS handler)
                        int carry = 0;

                        // Also indicates modifying CARRY
                        _loadR = (_cpu._ir & 0x0008) == 0;
                        
                        // At this point the ALU has already done its operation but the shifter has not yet run.
                        // We need to set the CARRY bit that will be passed through the shifter appropriately.
                        
                        // Select carry input value based on carry control
                        switch(_cpu._ir & 0x30)
                        {
                            case 0x00:
                                // Nothing; CARRY unaffected.
                                carry = _carry;
                                break;

                            case 0x10:
                                carry = 0;  // Z
                                break;

                            case 0x20:
                                carry = 1;  // O
                                break;

                            case 0x30:
                                carry = (~_carry) & 0x1;  // C
                                break;
                        }

                        // Now modify the result based on the current ALU result
                        switch (_cpu._ir & 0x700)
                        {
                            case 0x000:
                            case 0x200:
                            case 0x700:
                                // COM, MOV, AND - Carry unaffected
                                break;

                            case 0x100:                                                                
                            case 0x300:
                            case 0x400:
                            case 0x500:
                            case 0x600:
                                // NEG, INC, ADC, SUB, ADD - invert the carry bit
                                if (_cpu._aluC0 != 0)
                                {
                                    carry = (~carry) & 0x1;
                                }
                                break;                                
                        }                        

                        // Tell the Shifter to do a Nova-style shift with the
                        // given carry bit.
                        Shifter.SetDNS(true, carry);                        

                        break; 

                    default:
                        throw new InvalidOperationException(String.Format("Unhandled emulator F2 {0}.", ef2));
                        break;
                }
            }

            protected override void ExecuteSpecialFunction2Late(MicroInstruction instruction)
            {
                EmulatorF2 ef2 = (EmulatorF2)instruction.F2;
                switch (ef2)
                {
                    case EmulatorF2.LoadDNS:
                        //
                        // Set SKIP and CARRY flip-flops based on the final result of the operation after having
                        // passed through the shifter.
                        //
                        ushort result = Shifter.Output;
                        int carry = Shifter.DNSCarry;
                        switch (_cpu._ir & 0x7)
                        {
                            case 0:
                                // None, SKIP is reset
                                _skip = 0;
                                break;

                            case 1:     // SKP
                                // Always skip
                                _skip = 1;
                                break;

                            case 2:     // SZC
                                // Skip if carry result is zero
                                _skip = (carry == 0) ? 1 : 0;
                                break;

                            case 3:     // SNC
                                // Skip if carry result is nonzero
                                _skip = carry;
                                break;

                            case 4:     // SZR
                                _skip = (result == 0) ? 1 : 0;
                                break;

                            case 5:     // SNR
                                _skip = (result != 0) ? 1 : 0;
                                break;

                            case 6:     // SEZ
                                _skip = (result == 0 || carry == 0) ? 1 : 0;
                                break;

                            case 7:     // SBN
                                _skip = (result != 0 && carry != 0) ? 1 : 0;
                                break;
                        }

                        if (_loadR)
                        {
                            // Write carry flag back.
                            _carry = carry;
                        }

                        break;
                }
            

            }

            // From Section 3, Pg. 31:
            // "The emulator has two additional bits of state, the SKIP and CARRY flip flops. CARRY is distinct from the
            // microprocessor’s ALUC0 bit, tested by the ALUCY function.  CARRY is set or cleared as a function of IR and
            // many other things(see section 3.1) when the DNS<-(do novel shifts, F2= 12B) function is executed.  In
            // particular, if IR[12] is true, CARRY will not change.  DNS also addresses R from (3-IR[3 - 4]), causes a store
            // into R unless IR[12] is set, and sets the SKIP flip flop if appropriate(see section 3.1).  The emulator
            // microcode increments PC by 1 at the beginning of the next emulated instruction if SKIP is set, using
            // BUS+SKIP(ALUF= 13B).  IR_ clears SKIP."
            //
            // NB: _skip is in the encapsulating AltoCPU class to make it easier to reference since the ALU needs to know about it.
            private int _carry;
        }
    }
}