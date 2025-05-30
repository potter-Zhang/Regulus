﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Regulus.Core.Ssa.Instruction
{
    public class CmpBranchInstruction : AbstractInstruction
    {
        private Operand _op1;
        private Operand _op2;
        public BasicBlock Target1;
        public BasicBlock Target2;
        public CmpBranchInstruction(AbstractOpCode code, Operand op1, Operand op2, BasicBlock target1, BasicBlock target2) : base(code, InstructionKind.CmpBranch)
        {
            _op1 = op1;
            _op2 = op2;
            Target1 = target1;
            Target2 = target2;
        }

        public override bool HasLeftHandSideOperand()
        {
            return true;
        }

        public override int LeftHandSideOperandCount()
        {
            return 2;
        }

        public override Operand GetLeftHandSideOperand(int index)
        {
            return index == 0 ? _op1 : _op2;
        }

        public override void SetLeftHandSideOperand(int index, Operand operand)
        {
            if (index == 0)
            {
                _op1 = operand;
            }
            else
            {
                _op2 = operand;
            }
        }

        public override int BranchTargetCount()
        {
            return 2;
        }

        public override BasicBlock GetBranchTarget(int index)
        {
            return index == 0 ? Target1 : Target2;
        }

        public override void SetBranchTarget(int index, BasicBlock newTarget)
        {
            if (index == 0)
            {
                Target1 = newTarget;
            }
            else
            {
                Target2 = newTarget;
            }
        }

        public override string ToString()
        {
            return $"{base.ToString()}[{Target1.Index}][{Target2.Index}]";
        }
    }
}
