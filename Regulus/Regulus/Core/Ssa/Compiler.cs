﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mono.Cecil.Cil;
using NUnit.Framework;
using Regulus.Core.Ssa.Instruction;
using Regulus.Inject;
using static System.Reflection.Metadata.BlobBuilder;


namespace Regulus.Core.Ssa
{
    
    class OperandComparer : IEqualityComparer<Operand>
    {
        public bool Equals(Operand x, Operand y)
        {
            if (x == null || y == null)
                return false;

            return x.Index == y.Index && x.Kind == y.Kind && x.Version == y.Version;
        }

        public int GetHashCode(Operand obj)
        {
            if (obj == null)
                return 0;

            int hash = 17;
            hash = hash * 23 + obj.Index.GetHashCode();
            hash = hash * 23 + obj.Kind.GetHashCode();
            hash = hash * 23 + obj.Version.GetHashCode();

            return hash;
        }
    }
    public class Compiler
    {
        private struct PatchInfo
        {
            public int Start;
            public int Offset;
        }

        private class ArgumentGroup
        {
            public List<Operand> operands;
            public BasicBlock block;
            public AbstractInstruction instruction;
        }

        private Emitter _emitter;        
        private List<PatchInfo> _backPatchIndex;
        private Dictionary<Operand, List<Operand>> _interferenceGraph;
        private SsaBuilder _ssaBuilder;
        private int _currentMethodIndex;


        public Compiler()
        {
            _emitter = new Emitter();
        }

        public byte[] GetByteCode()
        {
            return _emitter.GetBytes().ToArray();
        }

        public Stream GetMeta()
        {
            return _emitter.GetMeta();
        }
        public byte[] Compile(int index, List<BasicBlock> blocks, SsaBuilder ssaBuilder, int argCount)
        {
            _emitter.Init(index);
            _ssaBuilder = ssaBuilder;
            _backPatchIndex = new List<PatchInfo>();            
            Dictionary<int, int> basicBlockStartOffset = new Dictionary<int, int>();
            List<AbstractInstruction> instructions = new List<AbstractInstruction>();
            
            Dictionary<Operand, List<BitArray>> liveRanges = BuildLiveRanges(blocks);
            foreach (var bb in blocks)
            {
                instructions.AddRange(bb.Instructions);
            }
            Dictionary<Operand, BitArray> splitLiveRanges = ReAllocOperands(liveRanges, instructions);
            Dictionary<Operand, List<Operand>> edges = BuildInterferenceGraph(instructions, liveRanges);
            List<Operand> localArguments = CollectLocalArguments(blocks.First(), argCount);
            List<ArgumentGroup> argumentGroups = CollectFunctionArguments(blocks);
            foreach (BasicBlock bb in blocks)
            {
                Console.WriteLine(bb);
            }

            AllocateRegister(edges, argumentGroups, localArguments, blocks);

            foreach (BasicBlock bb in blocks)
            {
                Console.WriteLine(bb);
            }

            foreach (BasicBlock block in blocks)
            {
                basicBlockStartOffset.Add(block.Index, _emitter.GetByteCount());
                foreach (AbstractInstruction instruction in block.Instructions)
                {
                    switch (instruction.Kind)
                    {
                        case InstructionKind.Call:
                            CompileCallInstruction((CallInstruction)instruction);
                            break;
                        case InstructionKind.Transform:
                            CompileTransformInstruction((TransformInstruction)instruction);
                            break;
                        case InstructionKind.UnCondBranch:
                            UnCondBranchInstruction br = (UnCondBranchInstruction)instruction;
                            _backPatchIndex.Add(new PatchInfo() { Start = _emitter.GetByteCount(), Offset = sizeof(OpCode) });
                            _emitter.EmitPInstruction(OpCode.Br, br.Target.Index);
                            break;
                        case InstructionKind.CondBranch:
                            CompilerCondBranchInstruction((CondBranchInstruction)instruction);

                            break;
                        case InstructionKind.CmpBranch:
                            CompileCmpBranchInstruction((CmpBranchInstruction)instruction);
                            break;
                        case InstructionKind.Return:
                            CompileReturnInstruction((ReturnInstruction)instruction);
                            break;
                        case InstructionKind.Move:
                            CompileMoveInstruction((MoveInstruction)instruction);
                            break;

                    }
                }
            }
            BackPatch(basicBlockStartOffset);
            
            return _emitter.GetBytes().ToArray();
        }

        public void CompileTo(string patchPath)
        {
            _emitter.EmitTypeMethodInfoToMeta();
            using (FileStream fstream = File.OpenWrite(patchPath))
            {
                _emitter.GetMeta().CopyTo(fstream);
            }
        }

        private void CompileReturnInstruction(ReturnInstruction returnInstruction)
        {
            if (returnInstruction.HasLeftHandSideOperand())
            {
                Operand op = returnInstruction.GetLeftHandSideOperand(0);
                if (op.Kind == OperandKind.Const)
                {
                    ValueOperand valueOperand = (ValueOperand)op;
                    _emitter.EmitOpCode(OpCode.Retc);
                    byte[] value = valueOperand.GetValue();
                    
                    _emitter.EmitBytes(value);
                    if (value.Length < sizeof(long))
                    {
                        _emitter.EmitInt(0);
                    }
                }
                else 
                    _emitter.EmitAInstruction(OpCode.Ret, ComputeRegisterLocation(returnInstruction.GetLeftHandSideOperand(0)));
            }
            else
            {
                _emitter.EmitAInstruction(OpCode.Ret, 0);
            }
        }

        private void CompilerCondBranchInstruction(CondBranchInstruction condBranchInstruction)
        {
            _backPatchIndex.Add(new PatchInfo() { Start = _emitter.GetByteCount(), Offset = sizeof(OpCode) + sizeof(byte) });
            _backPatchIndex.Add(new PatchInfo() { Start = _emitter.GetByteCount(), Offset = sizeof(OpCode) + sizeof(byte) + sizeof(int)});
            if (condBranchInstruction.Code == AbstractOpCode.BrFalse)
            {
                _emitter.EmitAPPInstruction(                
                OpCode.BrFalse,
                ComputeRegisterLocation(condBranchInstruction.GetLeftHandSideOperand(0)),
                condBranchInstruction.GetBranchTarget(0).Index,
                condBranchInstruction.GetBranchTarget(1).Index);
            }
            else
            {
                _emitter.EmitAPPInstruction(
                OpCode.BrTrue,
                ComputeRegisterLocation(condBranchInstruction.GetLeftHandSideOperand(0)),
                condBranchInstruction.GetBranchTarget(0).Index,
                condBranchInstruction.GetBranchTarget(1).Index);
            }

        }

        private void CompileMoveInstruction(MoveInstruction moveInstruction)
        {
            Operand op1 = moveInstruction.GetLeftHandSideOperand(0);
            Operand op2 = moveInstruction.GetRightHandSideOperand(0);

            //if (moveInstruction.Code == AbstractOpCode.Dup)
            if (op1.Kind == OperandKind.Const)
            {
                ValueOperand value = op1 as ValueOperand;
                switch (value.OpType)
                {
                    case ValueOperandType.Integer:
                        _emitter.EmitAPInstruction(OpCode.Ldc_Int,
                            ComputeRegisterLocation(op2),
                            value.GetInt());
                        break;
                    case ValueOperandType.Long:
                        _emitter.EmitAPInstruction(OpCode.Ldc_Long,
                            ComputeRegisterLocation(op2),
                            value.GetValue());
                        break;
                    case ValueOperandType.Float:
                        _emitter.EmitAPInstruction(OpCode.Ldc_Float,
                            ComputeRegisterLocation(op2),
                            value.GetFloat());
                        break;
                    case ValueOperandType.Double:
                        _emitter.EmitAPInstruction(OpCode.Ldc_Double,
                            ComputeRegisterLocation(op2),
                            value.GetValue());
                        break;
                    case ValueOperandType.String:
                        _emitter.EmitAPInstruction(OpCode.LdStr,
                            ComputeRegisterLocation(op2),
                            value.GetStringIndex());
                        break;
                    case ValueOperandType.LocalPointer:
                        _emitter.EmitAPInstruction(
                            OpCode.Ldloca,                            
                            ComputeRegisterLocation(op2),
                            value.GetInt());
                        break;
                    case ValueOperandType.ArgPointer:
                        _emitter.EmitAPInstruction(
                            OpCode.Ldarga,
                            ComputeRegisterLocation(op2),
                            value.GetInt());
                        break;


                }
            }
            else if (moveInstruction.Code == AbstractOpCode.Ldloca)
            {
                _emitter.EmitABInstruction(
                    OpCode.Ldloca,
                    ComputeRegisterLocation(op1),
                    ComputeRegisterLocation(op2));
            }
            else
            {
                _emitter.EmitABInstruction(
                                OpCode.Mov,
                                ComputeRegisterLocation(op1),
                                ComputeRegisterLocation(op2));

            }
        }

        

        private void BackPatch(Dictionary<int, int> basicBlockStartIndex)
        {
            List<byte> bytecode = _emitter.GetBytes();
            foreach (PatchInfo i in _backPatchIndex)
            {
                int basicBlockIndex = BitConverter.ToInt32(bytecode.GetRange(i.Start + i.Offset, 4).ToArray());
                int target = basicBlockStartIndex[basicBlockIndex];
                int offset = target - i.Start;
                byte[] bytes = BitConverter.GetBytes(offset);
                for (int j = 0; j < 4; j++)
                {
                    bytecode[j + i.Start + i.Offset] = bytes[j];
                }
            }
        }

        private void CompileInvokeInstruction(CallInstruction callInstruction)
        {
            List<string> parameterTypes = callInstruction.ParameterTypesWithoutImplicitParameter.Select(t => t.AssemblyQualifiedName).ToList();
            List<bool> isRefTypes = callInstruction.ParametersType.Select(t => t.IsByRef).ToList();
            //int argCount = callInstruction.ArgCount - (callInstruction.IsStructConstructor ? 1 : 0);
            int methodIndex = _emitter.AddMethod(
                callInstruction.DeclaringType,
                callInstruction.Method,
                callInstruction.IsGenericMethod,
                callInstruction.CallVirt,
                parameterTypes);
            if (callInstruction.IsStructConstructor)
            {
                throw new NotImplementedException();
                _emitter.EmitABPPInstruction(
                OpCode.Call,
                callInstruction.HasLeftHandSideOperand() ?
                ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(1)) :
                (byte)0,
                callInstruction.HasLeftHandSideOperand() ?
                ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(0)) :
                (byte)0,
                methodIndex,
                callInstruction.ArgCount - 1
                );
                WriteParameterTypes(callInstruction.ArgCount - 1, callInstruction.ParameterTypesWithoutImplicitParameter);
                _emitter.EmitType(Constants.ObjectPointer);
                WriteRefArgInfo(isRefTypes, callInstruction, callInstruction.ArgCount - 1);
            }
            else
            {
                // 2 byte | 4 bytes     | 1 byte    | 2 * (argCount + 1) bytes                                                | argCount bytes | 9 + 3 * argCount bytes
                // opcode | methodIndex | argCount  | argCount * [parameterType, parameterIndex] + [returnType, returnIndex]  | isRefInfo      | Total
                _emitter.EmitOpCode(OpCode.Call);
                _emitter.EmitByte((byte)callInstruction.ArgCount);
                _emitter.EmitInt(methodIndex);

                for (int i = 0; i < callInstruction.ArgCount; i++)
                {
                    WriteType(callInstruction.ParametersType[i]);
                    _emitter.EmitByte(ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(i)));                    
                }
                WriteType(callInstruction.ReturnType);
                if (callInstruction.HasRightHandSideOperand())
                {
                    _emitter.EmitByte(ComputeRegisterLocation(callInstruction.GetRightHandSideOperand(0)));
                }              

                WriteRefArgInfo(isRefTypes, callInstruction, callInstruction.ArgCount);
            }
        }

        private void WriteParameterIndex(CallInstruction callInstruction)
        {
            // also deal with referencing same argument?
            for (int i = 0; i < callInstruction.ArgCount; i++)
            {
                _emitter.EmitByte(ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(i)));
            }
        }

        private void CompileCallInstruction(CallInstruction callInstruction)
        {
            List<string> parameterTypes = callInstruction.ParameterTypesWithoutImplicitParameter.Select(t => t.AssemblyQualifiedName).ToList();
            List<bool> isRefTypes = callInstruction.ParametersType.Select(t => t.IsByRef).ToList();
            //int argCount = callInstruction.ArgCount - (callInstruction.IsStructConstructor ? 1 : 0);
            if (callInstruction.IsNewMethod)
            {
                // [startReg:byte][returnReg:byte][methodIndex:int]
                int newMethodIndex = TagFilter.GetMethodId(callInstruction.MethodDef);
                byte startReg = callInstruction.HasLeftHandSideOperand() ?
                    ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(0)) :
                    ComputeRegisterLocation(callInstruction.GetRightHandSideOperand(0));
                byte returnReg = callInstruction.HasRightHandSideOperand() ?
                    ComputeRegisterLocation(callInstruction.GetRightHandSideOperand(0)) :
                    (byte)(startReg + callInstruction.ArgCount);
                _emitter.EmitABPInstruction(
                    OpCode.Calln,
                    startReg,
                    (byte)(returnReg - startReg),
                    newMethodIndex
                    );
                return;
            }
            int methodIndex = _emitter.AddMethod(
                callInstruction.DeclaringType,
                callInstruction.Method,
                callInstruction.IsGenericMethod,
                callInstruction.CallVirt,
                parameterTypes);
            //if (callInstruction.IsStructConstructor)
            //{
            //    _emitter.EmitABPPInstruction(
            //    OpCode.Call,
            //    callInstruction.HasLeftHandSideOperand() ?
            //    ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(1)) :
            //    (byte)0,
            //    callInstruction.HasLeftHandSideOperand() ?
            //    ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(0)) :
            //    (byte)0,
            //    methodIndex,
            //    callInstruction.ArgCount - 1
            //    );
            //    WriteParameterTypes(callInstruction.ArgCount - 1, callInstruction.ParameterTypesWithoutImplicitParameter);
            //    _emitter.EmitType(Constants.ObjectPointer);
            //    for (int i = 0; i < callInstruction.ArgCount - 1; i++)
            //    {
            //        _emitter.EmitBool(isRefTypes[i]);
            //    }
            //}
            //else
            //{
                _emitter.EmitABPPInstruction(
                OpCode.Call,
                callInstruction.HasLeftHandSideOperand() ?
                ComputeRegisterLocation(callInstruction.GetLeftHandSideOperand(0)) :
                (byte)0,
                callInstruction.HasRightHandSideOperand() ?
                ComputeRegisterLocation(callInstruction.GetRightHandSideOperand(0)) :
                (byte)0,
                methodIndex,
                callInstruction.ArgCount
                );
                WriteParameterTypes(callInstruction);

                if (callInstruction.IsConstructor)
                {
                    _emitter.EmitType(Constants.Object);
                }
                else
                {
                    WriteType(callInstruction.ReturnType);
                }

            WriteRefArgInfo(isRefTypes, callInstruction, isRefTypes.Count);
                
            //}

        }

        private void WriteRefArgInfo(List<bool> isRefType, CallInstruction callInstruction, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (isRefType[i])
                {
                    if (callInstruction.GetLeftHandSideOperand(i).OpType == ValueOperandType.ArgPointer)
                    {
                        _emitter.EmitType(Constants.PointerReference);
                    }
                    else
                    {
                        _emitter.EmitType(Constants.Reference);
                    }
                }

                else
                {
                    _emitter.EmitType(Constants.NoReference);
                }

            }
        }

        private void WriteType(Type type, ValueOperandType opType = ValueOperandType.Unknown)
        {
            //byte isAddress = 0;
            byte typeInfo = 0;
            //if (opType == ValueOperandType.ArgPointer || opType == ValueOperandType.ArgPointer)
            //{
            //    isAddress = Constants.Address;
            //}
            if (type.IsByRef)
            {
                type = type.GetElementType();
            }
            if (type == typeof(byte))
            {
                typeInfo = Constants.Byte;
            }
            else if (type == typeof(sbyte))
            {
                typeInfo = Constants.Sbyte;
            }
            else if (type == typeof(bool))
            {
                typeInfo = Constants.Bool;
            }
            else if (type == typeof(ushort))
            {
                typeInfo = Constants.UShort;
            }
            else if (type == typeof(short))
            {
                typeInfo = Constants.Short;
            }
            else if (type == typeof(int))
            {
                typeInfo = Constants.Int;
            }
            else if (type == typeof(uint))
            {
                typeInfo = Constants.UInt;
            }
            else if (type == typeof(long))
            {
                typeInfo = Constants.Long;
            }
            else if (type == typeof(ulong))
            {
                typeInfo = Constants.ULong;
            }
            else if (type == typeof(float))
            {
                typeInfo = Constants.Float;
            }
            else if (type == typeof(double))
            {
                typeInfo = Constants.Double;
            }
            else if (type == typeof(void))
            {
                typeInfo = Constants.Void;
            }
            else
            {             
                typeInfo = Constants.Object;
            }
            _emitter.EmitType((byte)(typeInfo));
        }

        private void WriteParameterTypes(CallInstruction callInstruction)
        {
            for (int i = 0; i < callInstruction.ArgCount; i++)
            {
                if (callInstruction.ParametersType[i].IsByRef && callInstruction.GetLeftHandSideOperand(i).GetOriginalOp().Version == Operand.DefaultVersion)
                {
                    _emitter.EmitType(Constants.Out);
                    continue;
                }
                Type type = callInstruction.ParametersType[i];
                WriteType(type);
            }
        }

        private void WriteParameterTypes(int argCount, List<Type> argTypes)
        {
            for (int i = 0; i < argCount; i++)
            {
                Type type = argTypes[i];
                WriteType(type);
            }
        }


        private void CompileCmpBranchInstruction(CmpBranchInstruction instruction)
        {
            Operand op1 = instruction.GetLeftHandSideOperand(0);
            Operand op2 = instruction.GetLeftHandSideOperand(1);
            int target = instruction.GetBranchTarget(0).Index;
            int start = _emitter.GetByteCount();
            int offset = 0;
            if (op1.Kind == OperandKind.Const)
            {
                ValueOperand value = op1 as ValueOperand;

                _emitter.EmitOpCode(GetOpCodeWithConst(instruction.Code, op1.OpType));
                _emitter.EmitByte(ComputeRegisterLocation(op2));
                _emitter.EmitBytes(value.GetValue());
                offset = _emitter.GetByteCount() - start;
                _emitter.EmitInt(target);
                
            }
            else if (op2.Kind == OperandKind.Const)
            {
                ValueOperand value = op2 as ValueOperand;
                _emitter.EmitOpCode(GetReverseOpCodeWithConst(instruction.Code, op2.OpType));
                _emitter.EmitByte(ComputeRegisterLocation(op1));
                _emitter.EmitBytes(value.GetValue());
                offset = _emitter.GetByteCount() - start;
                _emitter.EmitInt(target);
            }
            else
            {
                _emitter.EmitABPInstruction(
                    GetOpCodeWithoutConst(instruction.Code, op1.OpType),
                    ComputeRegisterLocation(op2),
                    ComputeRegisterLocation(op1),
                    target);
                offset = sizeof(OpCode) + 2 * sizeof(byte);
            }
            _backPatchIndex.Add(new PatchInfo() { Start = start, Offset = offset });
            
        }

        private OpCode GetOpCodeWithoutConst(AbstractOpCode code, ValueOperandType opType)
        {
            switch (code)
            {
                case AbstractOpCode.Add:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Add_Int;
                        case ValueOperandType.Long:
                            return OpCode.Add_Long;
                        case ValueOperandType.Float:
                            return OpCode.Add_Float;
                        case ValueOperandType.Double:
                            return OpCode.Add_Double;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Add_Ovf:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Add_Ovf_Int;
                        case ValueOperandType.Long:
                            return OpCode.Add_Ovf_Long;
                        case ValueOperandType.Float:
                            return OpCode.Add_Ovf_Float;
                        case ValueOperandType.Double:
                            return OpCode.Add_Ovf_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Add_Ovf_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Add_Ovf_UInt;
                        case ValueOperandType.Long:
                            return OpCode.Add_Ovf_ULong;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Sub_Int;
                        case ValueOperandType.Long:
                            return OpCode.Sub_Long;
                        case ValueOperandType.Float:
                            return OpCode.Sub_Float;
                        case ValueOperandType.Double:
                            return OpCode.Sub_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub_Ovf:
                    switch (opType)
                    {
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub_Ovf_Un:
                    switch (opType)
                    {
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Or:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Or_Int;
                        case ValueOperandType.Long:
                            return OpCode.Or_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.And:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.And_Int;
                        case ValueOperandType.Long:
                            return OpCode.And_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Xor:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Xor_Int;
                        case ValueOperandType.Long:
                            return OpCode.Xor_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Mul:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Mul_Int;
                        case ValueOperandType.Long:
                            return OpCode.Mul_Long;
                        case ValueOperandType.Float:
                            return OpCode.Mul_Float;
                        case ValueOperandType.Double:
                            return OpCode.Mul_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Mul_Ovf:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Mul_Ovf_Int;
                        case ValueOperandType.Long:
                            return OpCode.Mul_Ovf_Long;
                        case ValueOperandType.Float:
                            return OpCode.Mul_Ovf_Float;
                        case ValueOperandType.Double:
                            return OpCode.Mul_Ovf_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Mul_Ovf_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Mul_Ovf_UInt;
                        case ValueOperandType.Long:
                            return OpCode.Mul_Ovf_ULong;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Div:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Div_Int;
                        case ValueOperandType.Long:
                            return OpCode.Div_Long;
                        case ValueOperandType.Float:
                            return OpCode.Div_Float;
                        case ValueOperandType.Double:
                            return OpCode.Div_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Div_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Div_UInt;
                        case ValueOperandType.Long:
                            return OpCode.Div_ULong;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Rem:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Rem_Int;
                        case ValueOperandType.Long:
                            return OpCode.Rem_Long;
                        case ValueOperandType.Float:
                            return OpCode.Rem_Float;
                        case ValueOperandType.Double:
                            return OpCode.Rem_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Rem_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Rem_UInt;
                        case ValueOperandType.Long:
                            return OpCode.Rem_ULong;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shl:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Shl_Int;
                        case ValueOperandType.Long:
                            return OpCode.Shl_Long;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shr:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Shr_Int;
                        case ValueOperandType.Long:
                            return OpCode.Shr_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shr_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Shr_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Shr_Un_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Cgt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Cgt_Int;
                        case ValueOperandType.Long:
                            return OpCode.Cgt_Long;
                        case ValueOperandType.Float:
                            return OpCode.Cgt_Float;
                        case ValueOperandType.Double:
                            return OpCode.Cgt_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Clt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Clt_Int;
                        case ValueOperandType.Long:
                            return OpCode.Clt_Long;
                        case ValueOperandType.Float:
                            return OpCode.Clt_Float;
                        case ValueOperandType.Double:
                            return OpCode.Clt_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Cgt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Cgt_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Cgt_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Cgt_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Cgt_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bgt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Bgt_Int;
                        case ValueOperandType.Long:
                            return OpCode.Bgt_Long;
                        case ValueOperandType.Float:
                            return OpCode.Bgt_Float;
                        case ValueOperandType.Double:
                            return OpCode.Bgt_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bgt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Bgt_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Bgt_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Bgt_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Bgt_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ble:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Ble_Int;
                        case ValueOperandType.Long:
                            return OpCode.Ble_Long;
                        case ValueOperandType.Float:
                            return OpCode.Ble_Float;
                        case ValueOperandType.Double:
                            return OpCode.Ble_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ble_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Ble_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Ble_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Ble_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Ble_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bge_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Bge_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Bge_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Bge_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Bge_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ceq:
                    return OpCode.Ceq;
                case AbstractOpCode.Conv_I:
                case AbstractOpCode.Conv_I4:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_I4_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_I4_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_I4_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_I4_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_I1:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_I1_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_I1_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_I1_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_I1_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_I2:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_I2_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_I2_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_I2_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_I2_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_I8:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_I8_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_I8_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_I8_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_I8_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_U:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_U_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_U_Long;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_U1:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_U1_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_U1_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_U1_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_U1_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_U2:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_U2_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_U2_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_U2_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_U2_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_U4:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_U4_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_U4_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_U4_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_U4_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_U8:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_U8_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_U8_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_U8_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_U8_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_R4:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_R4_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_R4_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_R4_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_R4_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_R8:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_R8_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_R8_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_R8_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_R8_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_R_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_R_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_R_Un_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I4_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I4_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I4_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I4_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I1:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I1_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I1_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I1_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I1_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I2:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I2_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I2_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I2_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I2_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I8:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I8_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I8_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I8_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I8_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I1_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I1_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I1_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I1_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I1_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I2_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I2_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I2_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I2_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I2_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I4_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I4_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I4_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I4_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I4_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_I8_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_I8_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_I8_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_I8_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_I8_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_U1_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_U1_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_U1_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_U1_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_U1_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_U2_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_U2_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_U2_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_U2_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_U2_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_U_Un:
                case AbstractOpCode.Conv_Ovf_U4_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_U4_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_U4_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_U4_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_U4_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Conv_Ovf_U8_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Conv_Ovf_U8_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.Conv_Ovf_U8_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.Conv_Ovf_U8_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.Conv_Ovf_U8_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ldelem_I1:
                    return OpCode.Ldelem_I1;
                case AbstractOpCode.Ldelem_I2:
                    return OpCode.Ldelem_I2;
                case AbstractOpCode.Ldelem_I4:
                    return OpCode.Ldelem_I4;
                case AbstractOpCode.Ldelem_I8:
                    return OpCode.Ldelem_I8;
                case AbstractOpCode.Ldelem_R4:
                    return OpCode.Ldelem_R4;
                case AbstractOpCode.Ldelem_R8:
                    return OpCode.Ldelem_R8;
                case AbstractOpCode.Ldelem_U1:
                    return OpCode.Ldelem_U1;
                case AbstractOpCode.Ldelem_U2:
                    return OpCode.Ldelem_U2;
                case AbstractOpCode.Ldelem_U4:
                    return OpCode.Ldelem_U4;
                case AbstractOpCode.Ldelem_U8:
                    return OpCode.Ldelem_U8;
                case AbstractOpCode.Stelem_I4:
                    return OpCode.Stelem_I4;
                case AbstractOpCode.Ldlen:
                    return OpCode.Ldlen;
                case AbstractOpCode.Ldind_I4:
                    return OpCode.Ldind_I4;
                case AbstractOpCode.Stind_I1:
                    switch (opType)
                    {
                        case ValueOperandType.ArrayPointer:
                            return OpCode.Stind_I1_ArrayPointer;
                        case ValueOperandType.LocalPointer:
                            return OpCode.Stind_I1_LocalPointer;
                        case ValueOperandType.StaticFieldPointer:
                            return OpCode.Stind_I1_StaticFieldPointer;
                        case ValueOperandType.InstanceFieldPointer:
                            return OpCode.Stind_I1_InstanceFieldPointer;
                        default:
                            throw new NotImplementedException ();
                    }
                case AbstractOpCode.Stind_I2:
                    switch (opType)
                    {
                        case ValueOperandType.ArrayPointer:
                            return OpCode.Stind_I2_ArrayPointer;
                        case ValueOperandType.LocalPointer:
                            return OpCode.Stind_I2_LocalPointer;
                        case ValueOperandType.StaticFieldPointer:
                            return OpCode.Stind_I2_StaticFieldPointer;
                        case ValueOperandType.InstanceFieldPointer:
                            return OpCode.Stind_I2_InstanceFieldPointer;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Stind_I4:
                    switch (opType)
                    {
                        case ValueOperandType.ArrayPointer:
                            return OpCode.Stind_I4_ArrayPointer;
                        case ValueOperandType.LocalPointer:
                            return OpCode.Stind_I4_LocalPointer;
                        case ValueOperandType.StaticFieldPointer:
                            return OpCode.Stind_I4_StaticFieldPointer;
                        case ValueOperandType.InstanceFieldPointer:
                            return OpCode.Stind_I4_InstanceFieldPointer;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Stind_I8:
                    switch (opType)
                    {
                        case ValueOperandType.ArrayPointer:
                            return OpCode.Stind_I8_ArrayPointer;
                        case ValueOperandType.LocalPointer:
                            return OpCode.Stind_I8_LocalPointer;
                        case ValueOperandType.StaticFieldPointer:
                            return OpCode.Stind_I8_StaticFieldPointer;
                        case ValueOperandType.InstanceFieldPointer:
                            return OpCode.Stind_I8_InstanceFieldPointer;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Stind_R4:
                    switch (opType)
                    {
                        case ValueOperandType.ArrayPointer:
                            return OpCode.Stind_R4_ArrayPointer;
                        case ValueOperandType.LocalPointer:
                            return OpCode.Stind_R4_LocalPointer;
                        case ValueOperandType.StaticFieldPointer:
                            return OpCode.Stind_R4_StaticFieldPointer;
                        case ValueOperandType.InstanceFieldPointer:
                            return OpCode.Stind_R4_InstanceFieldPointer;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Stind_R8:
                    switch (opType)
                    {
                        case ValueOperandType.ArrayPointer:
                            return OpCode.Stind_R8_ArrayPointer;
                        case ValueOperandType.LocalPointer:
                            return OpCode.Stind_R8_LocalPointer;
                        case ValueOperandType.StaticFieldPointer:
                            return OpCode.Stind_R8_StaticFieldPointer;
                        case ValueOperandType.InstanceFieldPointer:
                            return OpCode.Stind_R8_InstanceFieldPointer;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ldelema:
                    return OpCode.Ldelema;
                case AbstractOpCode.Stelem_I1:
                    return OpCode.Stelem_I1;
                case AbstractOpCode.Stelem_I2:
                    return OpCode.Stelem_I2;

                case AbstractOpCode.Stelem_I8:
                    return OpCode.Stelem_I8;
                case AbstractOpCode.Stelem_R4:
                    return OpCode.Stelem_R4;
                case AbstractOpCode.Stelem_R8:
                    return OpCode.Stelem_R8;
                case AbstractOpCode.Blt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Blt_Int;
                        case ValueOperandType.Long:
                            return OpCode.Blt_Long;
                        case ValueOperandType.Float:
                            return OpCode.Blt_Float;
                        case ValueOperandType.Double:
                            return OpCode.Blt_Double;
                        default:
                            throw new NotImplementedException();
                    }
                default:
                    throw new NotImplementedException();
            }
        }

        private OpCode GetOpCodeWithConst(AbstractOpCode code, ValueOperandType opType)
        {
            switch (code)
            {
                case AbstractOpCode.Add:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.AddI_Int;
                        case ValueOperandType.Long:
                            return OpCode.AddI_Long;
                        case ValueOperandType.Float:
                            return OpCode.AddI_Float;
                        case ValueOperandType.Double:
                            return OpCode.AddI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Add_Ovf:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.AddI_Ovf_Int;
                        case ValueOperandType.Long:
                            return OpCode.AddI_Ovf_Long;
                        case ValueOperandType.Float:
                            return OpCode.AddI_Ovf_Float;
                        case ValueOperandType.Double:
                            return OpCode.AddI_Ovf_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Add_Ovf_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.AddI_Ovf_UInt;
                        case ValueOperandType.Long:
                            return OpCode.AddI_Ovf_ULong;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.SubI_Int;
                        case ValueOperandType.Long:
                            return OpCode.SubI_Long;
                        case ValueOperandType.Float:
                            return OpCode.SubI_Float;
                        case ValueOperandType.Double:
                            return OpCode.SubI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub_Ovf:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.SubI_Ovf_Int;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub_Ovf_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.SubI_Ovf_Int;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Or:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.OrI_Int;
                        case ValueOperandType.Long:
                            return OpCode.OrI_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.And:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.OrI_Int;
                        case ValueOperandType.Long:
                            return OpCode.OrI_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Xor:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.XorI_Int;
                        case ValueOperandType.Long:
                            return OpCode.XorI_Long;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Mul:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.MulI_Int;
                        case ValueOperandType.Long:
                            return OpCode.MulI_Long;
                        case ValueOperandType.Float:
                            return OpCode.MulI_Float;
                        case ValueOperandType.Double:
                            return OpCode.MulI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Mul_Ovf:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.MulI_Int;
                        case ValueOperandType.Long:
                            return OpCode.MulI_Long;
                        case ValueOperandType.Float:
                            return OpCode.MulI_Float;
                        case ValueOperandType.Double:
                            return OpCode.MulI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Mul_Ovf_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.MulI_Ovf_Int;
                        case ValueOperandType.Long:
                            return OpCode.MulI_Ovf_Long;
                        case ValueOperandType.Float:
                            return OpCode.MulI_Ovf_Float;
                        case ValueOperandType.Double:
                            return OpCode.MulI_Ovf_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Div:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.DivI_Int;
                        case ValueOperandType.Long:
                            return OpCode.DivI_Long;
                        case ValueOperandType.Float:
                            return OpCode.DivI_Float;
                        case ValueOperandType.Double:
                            return OpCode.DivI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Div_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.DivI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.DivI_Un_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Rem:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.RemI_Int;
                        case ValueOperandType.Long:
                            return OpCode.RemI_Long;
                        case ValueOperandType.Float:
                            return OpCode.RemI_Float;
                        case ValueOperandType.Double:
                            return OpCode.RemI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Rem_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.RemI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.RemI_Un_Long;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shl:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.ShlI_Int;
                        case ValueOperandType.Long:
                            return OpCode.ShlI_Long;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shr:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.Shr_Int;
                        case ValueOperandType.Long:
                            return OpCode.Shr_Long;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bgt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgtI_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgtI_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgtI_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgtI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bgt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgtI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgtI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgtI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgtI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Cgt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.CgtI_Int;
                        case ValueOperandType.Long:
                            return OpCode.CgtI_Long;
                        case ValueOperandType.Float:
                            return OpCode.CgtI_Float;
                        case ValueOperandType.Double:
                            return OpCode.CgtI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Cgt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.CgtI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.CgtI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.CgtI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.CgtI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
               
                case AbstractOpCode.Ceq:
                    return OpCode.CeqI;

                case AbstractOpCode.Blt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BltI_Int;
                        case ValueOperandType.Long:
                            return OpCode.BltI_Long;
                        case ValueOperandType.Float:
                            return OpCode.BltI_Float;
                        case ValueOperandType.Double:
                            return OpCode.BltI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Blt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BltI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BltI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BltI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BltI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bge_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgeI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgeI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgeI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgeI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bne:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                        case ValueOperandType.Float:
                            return OpCode.BneI_B4;
                        case ValueOperandType.Long:                           
                        case ValueOperandType.Double:
                            return OpCode.BneI_B8;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ble:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BleI_Int;
                        case ValueOperandType.Long:
                            return OpCode.BleI_Long;
                        case ValueOperandType.Float:
                            return OpCode.BleI_Float;
                        case ValueOperandType.Double:
                            return OpCode.BleI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ble_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BleI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BleI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BleI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BleI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }


                default:
                    throw new NotImplementedException();
            }
        }

        private OpCode GetReverseOpCodeWithConst(AbstractOpCode code, ValueOperandType opType)
        {
            switch (code)
            {
                case AbstractOpCode.Sub:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.AddI_Int;
                        case ValueOperandType.Long:
                            return OpCode.AddI_Long;
                        case ValueOperandType.Float:
                            return OpCode.AddI_Float;
                        case ValueOperandType.Double:
                            return OpCode.AddI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Sub_Ovf:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.AddI_Ovf_Int;
                        case ValueOperandType.Long:
                            return OpCode.AddI_Ovf_Long;
                        case ValueOperandType.Float:
                            return OpCode.AddI_Ovf_Float;
                        case ValueOperandType.Double:
                            return OpCode.AddI_Ovf_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Div:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.DivI_Int_R;
                        case ValueOperandType.Long:
                            return OpCode.DivI_Long_R;
                        case ValueOperandType.Float:
                            return OpCode.DivI_Float_R;
                        case ValueOperandType.Double:
                            return OpCode.DivI_Double_R;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Div_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.DivI_Un_Int_R;
                        case ValueOperandType.Long:
                            return OpCode.DivI_Un_Long_R;
                        case ValueOperandType.Float:
                            return OpCode.DivI_Float_R;
                        case ValueOperandType.Double:
                            return OpCode.DivI_Double_R;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Rem:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.RemI_Int_R;
                        case ValueOperandType.Long:
                            return OpCode.RemI_Long_R;
                        case ValueOperandType.Float:
                            return OpCode.RemI_Float_R;
                        case ValueOperandType.Double:
                            return OpCode.RemI_Double_R;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Rem_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.RemI_Un_Int_R;
                        case ValueOperandType.Long:
                            return OpCode.RemI_Un_Long_R;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shl:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.ShlI_Int_R;
                        case ValueOperandType.Long:
                            return OpCode.ShlI_Long_R;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Shr:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.ShrI_Int_R;
                        case ValueOperandType.Long:
                            return OpCode.ShrI_Long_R;

                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Cgt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.CltI_Int;
                        case ValueOperandType.Long:
                            return OpCode.CltI_Long;
                        case ValueOperandType.Float:
                            return OpCode.CltI_Float;
                        case ValueOperandType.Double:
                            return OpCode.CltI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Cgt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.CltI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.CltI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.CltI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.CltI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bgt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BltI_Int;
                        case ValueOperandType.Long:
                            return OpCode.BltI_Long;
                        case ValueOperandType.Float:
                            return OpCode.BltI_Float;
                        case ValueOperandType.Double:
                            return OpCode.BltI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bgt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BltI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BltI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BltI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BltI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ble:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgeI_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgeI_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgeI_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgeI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Ble_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgeI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgeI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgeI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgeI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Bge_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BleI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BleI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BleI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BleI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }

                case AbstractOpCode.Ceq:
                    return OpCode.CeqI;

                case AbstractOpCode.Blt:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgtI_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgtI_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgtI_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgtI_Double;
                        default:
                            throw new NotImplementedException();
                    }
                case AbstractOpCode.Blt_Un:
                    switch (opType)
                    {
                        case ValueOperandType.Integer:
                            return OpCode.BgtI_Un_Int;
                        case ValueOperandType.Long:
                            return OpCode.BgtI_Un_Long;
                        case ValueOperandType.Float:
                            return OpCode.BgtI_Un_Float;
                        case ValueOperandType.Double:
                            return OpCode.BgtI_Un_Double;
                        default:
                            throw new NotImplementedException();
                    }
                
                default:
                    throw new NotImplementedException();
            }
        }



        private void EmitCommutativeBinaryInstructionWithConstCheck(
            AbstractOpCode code,
            Operand op1,
            Operand op2,
            Operand op3
            )
        {
            ValueOperand value = null;
            Operand operandWithConst = null;

            if (op1.Kind == OperandKind.Const)
            {
                value = op1 as ValueOperand;
                operandWithConst = op2;
            }
            else if (op2.Kind == OperandKind.Const)
            {
                value = op2 as ValueOperand;
                operandWithConst = op1;
            }

            if (value != null)
            {
                _emitter.EmitABPInstruction(
                    GetOpCodeWithConst(code, op1.OpType),
                    ComputeRegisterLocation(operandWithConst),
                    ComputeRegisterLocation(op3),
                    value.GetValue());
            }
            else
            {
                _emitter.EmitABCInstruction(
                    GetOpCodeWithoutConst(code, op1.OpType),
                    ComputeRegisterLocation(op1),
                    ComputeRegisterLocation(op2),
                    ComputeRegisterLocation(op3));
            }
        }

        private void EmitNonCommutativeBinaryInstructionWithConstCheck(
            AbstractOpCode code,
            Operand op1,
            Operand op2,
            Operand op3)
        {
            ValueOperand value = null;
            Operand operandWithConst = null;
            OpCode opcode = OpCode.Nop;

            if (op1.Kind == OperandKind.Const)
            {
                value = op1 as ValueOperand;
                operandWithConst = op2;
                opcode = GetOpCodeWithConst(code, op1.OpType);
                
            }
            else if (op2.Kind == OperandKind.Const)
            {
                value = op2 as ValueOperand;
                operandWithConst = op1;
                opcode = GetReverseOpCodeWithConst(code, op1.OpType);
                if (code == AbstractOpCode.Sub ||
                    code == AbstractOpCode.Sub_Ovf ||
                    code == AbstractOpCode.Sub_Ovf_Un)
                {
                    value.Neg();
                }
            }
            else
            {
                opcode = GetOpCodeWithoutConst(code, op1.OpType);
            }

            if (value != null)
            {
                _emitter.EmitABPInstruction(
                    opcode,
                    ComputeRegisterLocation(operandWithConst),
                    ComputeRegisterLocation(op3),
                    value.GetValue());
            }
            else
            {
                _emitter.EmitABCInstruction(
                    opcode,
                    ComputeRegisterLocation(op2),
                    ComputeRegisterLocation(op1),
                    ComputeRegisterLocation(op3));
            }
        }



        private void EmitConvertInstruction(TransformInstruction instruction)
        {
            Operand op1 = instruction.GetLeftHandSideOperand(0);
            _emitter.EmitABInstruction(
                GetOpCodeWithoutConst(instruction.Code, op1.OpType),
                ComputeRegisterLocation(op1),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)));
        }

        private void EmitBoxInstruction(TransformInstruction instruction)
        {
            // ABSPInstrucion
            MetaOperand meta = instruction.GetMetaOperand();
            Type type = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type " + meta.TypeName);

            _emitter.EmitABInstruction(
                OpCode.Box,
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)));

            WriteType(type);
        }

        private void EmitFieldInstruction(TransformInstruction instruction)
        {
            MetaOperand meta = instruction.GetMetaOperand();
            // this is a field reference
            Type declaringType = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type " + meta.TypeName);
            FieldInfo field = declaringType.GetField(meta.FieldName) ?? throw new Exception("Can not load field " + meta.FieldName);

            int fieldIndex = _emitter.AddInstanceField(declaringType.AssemblyQualifiedName, field.Name);

            
            if (instruction.Code == AbstractOpCode.Ldfld)
            {
                _emitter.EmitABPInstruction(
                OpCode.Ldfld,
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                fieldIndex);
            }
            else if (instruction.Code == AbstractOpCode.Stfld)
            {
                switch (instruction.GetLeftHandSideOperand(1).OpType)
                {
                    case ValueOperandType.LocalPointer:
                        _emitter.EmitABPInstruction(
                            OpCode.Stfld_LocalPointer,
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                            fieldIndex);
                        break;
                    case ValueOperandType.InstanceFieldPointer:
                        _emitter.EmitABPInstruction(
                            OpCode.Stfld_InstanceFieldPointer,
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                            fieldIndex);
                        break;
                    case ValueOperandType.StaticFieldPointer:
                        _emitter.EmitABPInstruction(
                            OpCode.Stfld_StaticFieldPointer,
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                            fieldIndex);
                        break;
                    case ValueOperandType.ArrayPointer:
                        _emitter.EmitABPInstruction(
                            OpCode.Stfld_ArrayPointer,
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                            fieldIndex);
                        break;
                    default:
                        _emitter.EmitABPInstruction(
                            OpCode.Stfld,
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                            ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                            fieldIndex);
                        break;
                }
                
            }
            else if (instruction.Code == AbstractOpCode.Stsfld)
            {
                _emitter.EmitAPInstruction(
                OpCode.Stsfld,
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                fieldIndex);
            }
            else
            {
                _emitter.EmitAPInstruction(
                    OpCode.Ldsfld,
                    ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                    fieldIndex);
            }

            WriteType(field.FieldType);

        }

        private void EmitCastClassInstruction(TransformInstruction instruction)
        {
            MetaOperand meta = instruction.GetMetaOperand();
            // this is a field reference
            Type classType = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type " + meta.TypeName);

            int typeIndex = _emitter.AddType(classType.AssemblyQualifiedName);

            _emitter.EmitABPInstruction(
                OpCode.Castclass,
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                typeIndex);

        }

        private void EmitInitobjInstruction(TransformInstruction instruction)
        {
            MetaOperand meta = instruction.GetMetaOperand();
            Type type = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type " + meta.TypeName);
            _emitter.EmitAPInstruction(
                OpCode.Initobj,
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                _emitter.AddType(meta.TypeName));
        }

        private void EmitLdelemInstruction(TransformInstruction instruction)
        {
            Operand op1 = instruction.GetLeftHandSideOperand(0);
            if (op1.Kind == OperandKind.Const)
            {
                ValueOperand value = op1 as ValueOperand;
                _emitter.EmitABPInstruction(
                    OpCode.Ldelem_I4I,
                    ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),                
                    ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                    value.GetInt()
                );
                return;
            }
            
            _emitter.EmitABCInstruction(
                GetOpCodeWithoutConst(instruction.Code, ValueOperandType.Unknown),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)));
        }

        private void EmitLdlenInstruction(TransformInstruction instruction)
        {
            _emitter.EmitABInstruction(
                GetOpCodeWithoutConst(instruction.Code, ValueOperandType.Unknown),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)));
        }

        private void EmitStelemInstruction(TransformInstruction instruction)
        {
            Operand op1 = instruction.GetLeftHandSideOperand(0);
            Operand op2 = instruction.GetLeftHandSideOperand(1);

            if (op1.Kind == OperandKind.Const)
            {
                ValueOperand value1 = op1 as ValueOperand;
                if (op2.Kind == OperandKind.Const)
                {
                    ValueOperand value2 = op2 as ValueOperand;

                    _emitter.EmitAPPInstruction(
                        OpCode.Stelem_I4II,
                        ComputeRegisterLocation(instruction.GetLeftHandSideOperand(2)),
                        value1.GetInt(),
                        value2.GetInt());
                    return;

                }
                else
                {
                    _emitter.EmitABPInstruction(
                        OpCode.Stelem_I4I,
                        ComputeRegisterLocation(instruction.GetLeftHandSideOperand(2)),
                        ComputeRegisterLocation(op2),
                        value1.GetInt());
                    return;
                }
            }
            _emitter.EmitABCInstruction(
                GetOpCodeWithoutConst(instruction.Code, ValueOperandType.Unknown),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(2)));
        }

        private void EmitNewarrInstruction(TransformInstruction instruction)
        {
            MetaOperand meta = instruction.GetMetaOperand();
            Type arrtype = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type " + meta.TypeName);
            int typeId = _emitter.AddType(arrtype.AssemblyQualifiedName);

            Operand leftOp = instruction.GetLeftHandSideOperand(0);

            if (leftOp.Kind == OperandKind.Const)
            {
                ValueOperand value = leftOp as ValueOperand;
                _emitter.EmitAPPInstruction(
                    OpCode.NewarrI,
                    ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                    typeId,
                    value.GetInt());
            }
            else
            {
                _emitter.EmitABPInstruction(
                OpCode.Newarr,
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                typeId);
            }
            
        }

        private void EmitLdindInstruction(TransformInstruction instruction)
        {
            _emitter.EmitABInstruction(
                GetOpCodeWithoutConst(instruction.Code, ValueOperandType.Unknown),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)));
            //_emitter.EmitBool(instruction.NeedFreePointer);
        }

        private void EmitLdelemaInstruction(TransformInstruction instruction)
        {
            MetaOperand meta = instruction.GetMetaOperand();

            Type type = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type + " + meta.TypeName);
            int typeId = _emitter.AddType(type.AssemblyQualifiedName);

            _emitter.EmitABCPInstruction(
                GetOpCodeWithoutConst(instruction.Code, ValueOperandType.Unknown),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                typeId);
        }

        private void EmitStindInstruction(TransformInstruction instruction)
        {
            _emitter.EmitABInstruction(
                GetOpCodeWithoutConst(instruction.Code, instruction.GetLeftHandSideOperand(1).OpType),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(1)),
                ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)));
            //_emitter.EmitBool(instruction.NeedFreePointer);
        }

        private void EmitFieldAddressInstruction(TransformInstruction instruction)
        {
            MetaOperand meta = instruction.GetMetaOperand();
            // this is a field reference
            Type declaringType = Type.GetType(meta.TypeName) ?? throw new Exception("Can not load type " + meta.TypeName);
            FieldInfo field = declaringType.GetField(meta.FieldName) ?? throw new Exception("Can not load field " + meta.FieldName);

            int fieldIndex = _emitter.AddInstanceField(declaringType.AssemblyQualifiedName, field.Name);
            
            if (instruction.Code == AbstractOpCode.Ldflda)
            {
                _emitter.EmitABPInstruction(
                    OpCode.Ldflda,
                    ComputeRegisterLocation(instruction.GetLeftHandSideOperand(0)),
                    ComputeRegisterLocation(instruction.GetRightHandSideOperand(0)),
                    fieldIndex);
            }
            else
            {
                // should be ldsflda
                _emitter.EmitPInstruction(
                    OpCode.Ldsflda,                    
                    fieldIndex);
            }
        }
        private void CompileTransformInstruction(TransformInstruction instruction)
        {
            // 2op => 1op
            Operand op1, op2, op3;
            ValueOperand value;

            switch (instruction.Code)
            {

                case AbstractOpCode.Add:
                case AbstractOpCode.Add_Ovf:
                case AbstractOpCode.Add_Ovf_Un:
                case AbstractOpCode.And:
                case AbstractOpCode.Or:
                case AbstractOpCode.Xor:
                case AbstractOpCode.Mul:
                case AbstractOpCode.Mul_Ovf:
                case AbstractOpCode.Mul_Ovf_Un:
                    EmitCommutativeBinaryInstructionWithConstCheck(
                        instruction.Code,
                        instruction.GetLeftHandSideOperand(0),
                        instruction.GetLeftHandSideOperand(1),
                        instruction.GetRightHandSideOperand(0));
                    break;
                case AbstractOpCode.Sub:
                case AbstractOpCode.Sub_Ovf:
                case AbstractOpCode.Sub_Ovf_Un:
                case AbstractOpCode.Div:
                case AbstractOpCode.Div_Un:
                case AbstractOpCode.Rem:
                case AbstractOpCode.Rem_Un:
                case AbstractOpCode.Shl:
                case AbstractOpCode.Shr:
                case AbstractOpCode.Shr_Un:
                case AbstractOpCode.Bgt:
                case AbstractOpCode.Bgt_Un:
                case AbstractOpCode.Cgt:
                case AbstractOpCode.Cgt_Un:
                case AbstractOpCode.Ceq:
                case AbstractOpCode.Clt:
                case AbstractOpCode.Clt_Un:
                    EmitNonCommutativeBinaryInstructionWithConstCheck(
                        instruction.Code,
                        instruction.GetLeftHandSideOperand(0),
                        instruction.GetLeftHandSideOperand(1),
                        instruction.GetRightHandSideOperand(0));
                    break;
                case AbstractOpCode.Conv_I:
                case AbstractOpCode.Conv_I1:
                case AbstractOpCode.Conv_I2:
                case AbstractOpCode.Conv_I4:
                case AbstractOpCode.Conv_I8:
                case AbstractOpCode.Conv_Ovf_I:
                case AbstractOpCode.Conv_Ovf_I1:
                case AbstractOpCode.Conv_Ovf_I1_Un:
                case AbstractOpCode.Conv_Ovf_I2:
                case AbstractOpCode.Conv_Ovf_I2_Un:
                case AbstractOpCode.Conv_Ovf_I4:
                case AbstractOpCode.Conv_Ovf_I4_Un:
                case AbstractOpCode.Conv_Ovf_I8:
                case AbstractOpCode.Conv_Ovf_I8_Un:
                case AbstractOpCode.Conv_Ovf_I_Un:
                case AbstractOpCode.Conv_Ovf_U:
                case AbstractOpCode.Conv_Ovf_U1:
                case AbstractOpCode.Conv_Ovf_U1_Un:
                case AbstractOpCode.Conv_Ovf_U2:
                case AbstractOpCode.Conv_Ovf_U2_Un:
                case AbstractOpCode.Conv_Ovf_U4:
                case AbstractOpCode.Conv_Ovf_U4_Un:
                case AbstractOpCode.Conv_Ovf_U8:
                case AbstractOpCode.Conv_Ovf_U8_Un:
                case AbstractOpCode.Conv_Ovf_U_Un:
                case AbstractOpCode.Conv_R4:
                case AbstractOpCode.Conv_R8:
                case AbstractOpCode.Conv_R_Un:
                    EmitConvertInstruction(instruction);
                    break;
                case AbstractOpCode.Box:
                case AbstractOpCode.Unbox:
                    EmitBoxInstruction(instruction);
                    break;
                case AbstractOpCode.Ldfld:
                case AbstractOpCode.Ldsfld:
                case AbstractOpCode.Stfld:
                case AbstractOpCode.Stsfld:
                    EmitFieldInstruction(instruction);
                    break;
                case AbstractOpCode.Ldflda:
                case AbstractOpCode.Ldsflda:
                    EmitFieldAddressInstruction(instruction);
                    break;
                case AbstractOpCode.Castclass:
                    EmitCastClassInstruction(instruction);
                    break;
                case AbstractOpCode.Initobj:
                    EmitInitobjInstruction(instruction);
                    break;
                case AbstractOpCode.Ldelem_I1:
                case AbstractOpCode.Ldelem_I2:
                case AbstractOpCode.Ldelem_I4:
                case AbstractOpCode.Ldelem_I8:
                case AbstractOpCode.Ldelem_R4:
                case AbstractOpCode.Ldelem_R8:
                case AbstractOpCode.Ldelem_U1:
                case AbstractOpCode.Ldelem_U2:
                case AbstractOpCode.Ldelem_U4:
                case AbstractOpCode.Ldelem_U8:
                    EmitLdelemInstruction(instruction); 
                    break;
                case AbstractOpCode.Ldlen:
                    EmitLdlenInstruction(instruction);
                    break;
                case AbstractOpCode.Stelem_I1:
                case AbstractOpCode.Stelem_I2:
                case AbstractOpCode.Stelem_I4:
                case AbstractOpCode.Stelem_I8:
                case AbstractOpCode.Stelem_R4:
                case AbstractOpCode.Stelem_R8:
                
                    EmitStelemInstruction(instruction); 
                    break;

                case AbstractOpCode.Newarr:
                    EmitNewarrInstruction(instruction);
                    break;
                case AbstractOpCode.Ldind_I4:
                    EmitLdindInstruction(instruction);
                    break;
                case AbstractOpCode.Ldelema:
                    EmitLdelemaInstruction(instruction);
                    break;
                case AbstractOpCode.Stind_I4:
                    EmitStindInstruction(instruction);
                    break;
               
                



            }
        }

        private byte ComputeRegisterLocation(Operand op)
        {
            switch (op.Kind)
            {
                case OperandKind.Arg:
                    return (byte)op.Index;               
                case OperandKind.Reg:
                    return ((byte)op.Index);
                default:
                    throw new NotImplementedException();

            }
        }

        private List<AbstractInstruction> CollectInstructions(
            List<BasicBlock> blocks,
            out Dictionary<AbstractInstruction, List<AbstractInstruction>> Pred,
            out Dictionary<AbstractInstruction, List<AbstractInstruction>> Succ)
        {
            List<AbstractInstruction> instructions = new List<AbstractInstruction>();
            Pred = new Dictionary<AbstractInstruction, List<AbstractInstruction>>();
            Succ = new Dictionary<AbstractInstruction, List<AbstractInstruction>>();


            foreach (BasicBlock b in blocks)
            {

                instructions.AddRange(b.Instructions);
                List<AbstractInstruction> predOfFirst = new List<AbstractInstruction>();
                foreach (BasicBlock pred in b.Predecessors)
                {
                    predOfFirst.Add(pred.Instructions.Last());
                }
                Pred.Add(b.Instructions.First(), predOfFirst);

                List<AbstractInstruction> succOfLast = new List<AbstractInstruction>();
                foreach (BasicBlock succ in b.Successors)
                {
                    succOfLast.Add(succ.Instructions.First());
                }

                Succ.Add(b.Instructions.Last(), succOfLast);

                for (int i = 0; i < b.Instructions.Count - 1; i++)
                {
                    Succ.Add(b.Instructions[i], new List<AbstractInstruction>() { b.Instructions[i + 1] });
                    Pred.Add(b.Instructions[i + 1], new List<AbstractInstruction>() { b.Instructions[i] });
                }
            }
            return instructions;
        }


        private Dictionary<Operand, BitArray> ComputeDefs(List<AbstractInstruction> instructions)
        {
            Dictionary<Operand, BitArray> defs = new Dictionary<Operand, BitArray>();

            for (int i = 0; i < instructions.Count; i++)
            {
                AbstractInstruction instruction = instructions[i];
                int count = instruction.RightHandSideOperandCount();

                for (int j = 0; j < count; j++)
                {
                    Operand def = instruction.GetRightHandSideOperand(j);
                    if (!defs.ContainsKey(def))
                    {
                        defs.Add(def, new BitArray(instructions.Count));

                    }
                    defs[def].Set(i, true);
                }

                if (instruction.Kind == InstructionKind.Call)
                {
                    CallInstruction call = (CallInstruction)instruction;
                    for (int k = 0; k < call.ArgCount; k++)
                    {
                        if (call.ParametersType[k].IsByRef)
                        {
                            Operand implicitDef = call.GetLeftHandSideOperand(k);
                            if (!defs.ContainsKey(implicitDef))
                            {
                                defs.Add(implicitDef, new BitArray(instructions.Count));

                            }
                            defs[implicitDef].Set(i, true);
                        }
                    }
                }
            }

            return defs;
        }



        private void PrintBitArray(BitArray bits)
        {
            for (int i = 0; i < bits.Count; i++)
            {
                Console.Write(bits[i] ? 1 : 0);
            }
            Console.WriteLine();
        }

        private void AddOperand(Dictionary<Operand, int> operands, Operand op, ref int counter)
        {
            if (!operands.ContainsKey(op))
            {
                operands.Add(op, counter++);
            }
        }
        public Dictionary<Operand, int> CollectOperand(List<BasicBlock> blocks)
        {
            Dictionary<Operand, int> operands = new Dictionary<Operand, int>(new OperandComparer());
            int counter = 0;
            foreach (BasicBlock block in blocks)
            {
                foreach (AbstractInstruction instruction in block.Instructions)
                {
                    int rightCount = instruction.RightHandSideOperandCount();
                    int leftCount = instruction.LeftHandSideOperandCount();
                    for (int i = 0; i < rightCount; i++)
                    {
                        AddOperand(operands, instruction.GetRightHandSideOperand(i), ref counter);
                    }
                    for (int i = 0; i < leftCount; i++)
                    {
                        AddOperand(operands, instruction.GetLeftHandSideOperand(i), ref counter);
                    }
                }
            }
            return operands;
        }

        private BitArray GenerateUseDefBitArray(AbstractInstruction instruction, Dictionary<Operand, int> operands, bool use)
        {
            BitArray result = new BitArray(operands.Count);
            if (use)
            {
                int leftCount = instruction.LeftHandSideOperandCount();
                for (int i = 0; i < leftCount; i++)
                {
                    result.Set(operands[instruction.GetLeftHandSideOperand(i)], true);
                }

                
            }
            else
            {
                int rightCount = instruction.RightHandSideOperandCount();
                for (int i = 0; i < rightCount; i++)
                {
                    result.Set(operands[instruction.GetRightHandSideOperand(i)], true);
                }

                if (instruction.Kind == InstructionKind.Call)
                {
                    CallInstruction call = (CallInstruction)instruction;
                    for (int i = 0; i < call.LeftHandSideOperandCount(); i++)
                    {
                        if (call.ParametersType[i].IsByRef)
                        {
                            result.Set(operands[instruction.GetLeftHandSideOperand(i)], true);
                        }
                    }
                }
            }
            return result;
        }

        public void DoLiveVariableAnalysis(
            List<BasicBlock> blocks,
            out Dictionary<AbstractInstruction, BitArray> liveIn,
            out Dictionary<AbstractInstruction, BitArray> liveOut,
            out Dictionary<Operand, int> operands)
        {
            liveIn = new Dictionary<AbstractInstruction, BitArray>();
            liveOut = new Dictionary<AbstractInstruction, BitArray>();

            List<AbstractInstruction> Changed =
                CollectInstructions(
                    blocks,
                out Dictionary<AbstractInstruction, List<AbstractInstruction>> pred,
                out Dictionary<AbstractInstruction, List<AbstractInstruction>> succ);

            operands = CollectOperand(blocks);

            int opCount = operands.Count;

            foreach (AbstractInstruction instruction in Changed)
            {
                liveOut.Add(instruction, new BitArray(opCount));
                liveIn.Add(instruction, new BitArray(opCount));
            }

            Dictionary<AbstractInstruction, BitArray> used = new Dictionary<AbstractInstruction, BitArray>();
            Dictionary<AbstractInstruction, BitArray> def = new Dictionary<AbstractInstruction, BitArray>();
            foreach (AbstractInstruction instruction in Changed)
            {
                used.Add(instruction, GenerateUseDefBitArray(instruction, operands, true));
                def.Add(instruction, GenerateUseDefBitArray(instruction, operands, false));
            }

            while (Changed.Count > 0)
            {
                AbstractInstruction i = Changed.Last();
                Changed.RemoveAt(Changed.Count - 1);

                liveOut[i].SetAll(false);

                foreach (AbstractInstruction s in succ[i])
                {
                    liveOut[i].Or(liveIn[s]);
                }

                var oldIn = new BitArray(liveIn[i]);
                BitArray outClone = new BitArray(liveOut[i]);
                BitArray usedClone = new BitArray(used[i]);

                liveIn[i] = usedClone.Or(outClone.And(def[i].Not()));
                def[i].Not();

                if (oldIn.Xor(liveIn[i]).HasAnySet())
                {
                    foreach (AbstractInstruction p in pred[i])
                    {
                        Changed.Add(p);
                    }
                }
            }
        }

        public void DoReachingDefinitionAnalysis(
            List<BasicBlock> blocks,
            out Dictionary<AbstractInstruction, BitArray> Out,
            out Dictionary<AbstractInstruction, BitArray> In,
            out Dictionary<AbstractInstruction, int> defLoc)
        {
            Out = new Dictionary<AbstractInstruction, BitArray>();
            In = new Dictionary<AbstractInstruction, BitArray>();

            List<AbstractInstruction> Changed =
                CollectInstructions(
                    blocks,
                out Dictionary<AbstractInstruction, List<AbstractInstruction>> pred,
                out Dictionary<AbstractInstruction, List<AbstractInstruction>> succ);


            int defCount = Changed.Count;

            defLoc = new Dictionary<AbstractInstruction, int>();
            for (int i = 0; i < Changed.Count; i++)
            {
                defLoc.Add(Changed[i], i);
            }

            foreach (AbstractInstruction instruction in Changed)
            {
                Out.Add(instruction, new BitArray(defCount));
                In.Add(instruction, new BitArray(defCount));
            }

            //Dictionary<AbstractInstruction, int> allInstructions = new Dictionary<AbstractInstruction, int>();
            //for (int i = 0; i < defCount; i++)
            //{
            //    allInstructions.Add(Changed[i], i);
            //}

            Dictionary<Operand, BitArray> defs = ComputeDefs(Changed);

            Dictionary<AbstractInstruction, BitArray> gen = new Dictionary<AbstractInstruction, BitArray>();
            Dictionary<AbstractInstruction, BitArray> kill = new Dictionary<AbstractInstruction, BitArray>();

            for (int i = 0; i < Changed.Count; i++)
            {
                BitArray genBits = new BitArray(defCount);
                BitArray killBits;
                if (Changed[i].HasRightHandSideOperand())
                {
                    BitArray defClone = new BitArray(defs[Changed[i].GetRightHandSideOperand(0)]);
                    genBits.Set(i, true);
                    killBits = defClone.And(genBits.Not());
                    genBits.Not();
                }
                else
                {
                    killBits = new BitArray(defCount);
                }
                kill.Add(Changed[i], killBits);
                gen.Add(Changed[i], genBits);

            }

            Changed.Reverse();
            while (Changed.Count > 0)
            {
                AbstractInstruction i = Changed.Last();
                Changed.RemoveAt(Changed.Count - 1);

                In[i].SetAll(false);

                foreach (AbstractInstruction instruction in pred[i])
                {
                    In[i].Or(Out[instruction]);
                }

                var oldOut = new BitArray(Out[i]);
                BitArray inClone = new BitArray(In[i]);

                if (i.RightHandSideOperandCount() > 0)
                {
                    BitArray genClone = new BitArray(gen[i]);

                    Out[i] = genClone.Or(inClone.And(kill[i].Not()));
                    kill[i].Not();
                }
                else
                {
                    Out[i] = inClone;
                }

                if (oldOut.Xor(Out[i]).HasAnySet())
                {
                    foreach (AbstractInstruction instruction in succ[i])
                    {
                        Changed.Add(instruction);
                    }
                }

            }
        }

        private BitArray ComputeLiveInRange(
            int op,
            Dictionary<AbstractInstruction, BitArray> liveIn,
            Dictionary<AbstractInstruction, int> defLoc)
        {
            BitArray result = new BitArray(liveIn.Count);
            foreach (KeyValuePair<AbstractInstruction, BitArray> kv in liveIn)
            {
                if (kv.Value.Get(op))
                {
                    result.Set(defLoc[kv.Key], true);
                }
            }
            return result;
        }

        private BitArray ComputeReachingDefinitionBeforeRange(
            int instruction,
            Dictionary<AbstractInstruction, BitArray> In,
            Dictionary<AbstractInstruction, int> defLoc)
        {
            BitArray result = new BitArray(In.Count);
            foreach (KeyValuePair<AbstractInstruction, BitArray> kv in In)
            {
                if (kv.Value.Get(instruction))
                {
                    result.Set(defLoc[kv.Key], true);
                }
            }

            return result;

        }

        private bool BitIntersect(BitArray a, BitArray b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a.Get(i) == b.Get(i))
                    return true;
            }
            return false;
        }

        private void CollapseLiveRanges(List<BitArray> liveRanges, BitArray newLiveRange)
        {
            foreach (BitArray liveRange in liveRanges)
            {
                if (BitIntersect(liveRange, newLiveRange))
                {
                    liveRange.Or(newLiveRange);
                    return;
                }
            }
            liveRanges.Add(newLiveRange);
        }

        private void InsertArgumentDefinitions(List<BasicBlock> blocks)
        {
            List<AbstractInstruction> instructions = new List<AbstractInstruction>();
            HashSet<Operand> localArguments = new HashSet<Operand>(new OperandComparer());

            foreach (BasicBlock block in blocks)
            {
                foreach (AbstractInstruction instruction in block.Instructions)
                {
                    int leftOpCount = instruction.LeftHandSideOperandCount();
                    for (int i = 0; i < leftOpCount; i++)
                    {
                        Operand op = instruction.GetLeftHandSideOperand(i);
                        if (op.Kind == OperandKind.Arg && op.Version == Operand.DefaultVersion)
                        {
                            localArguments.Add(op);
                        }

                    }
                    int rightOpCount = instruction.RightHandSideOperandCount();
                    for (int i = 0; i < rightOpCount; i++)
                    {
                        Operand op = instruction.GetRightHandSideOperand(i);
                        if (op.Kind == OperandKind.Arg && op.Version == Operand.DefaultVersion)
                        {
                            localArguments.Add(op);
                        }

                    }
                }
            }
            List<Operand> localArgumentList = localArguments.ToList();
            for (int i = 0; i < localArgumentList.Count; i++) 
            {
                AbstractInstruction instruction = new TransformInstruction(AbstractOpCode.Nop).AddRightOperand(localArgumentList[i]);
                instructions.Add(instruction);                
            }
            blocks.First().Instructions.InsertRange(0, instructions);
        }

        private void RemoveArgumentDefinitions(BasicBlock firstBlock, int argumentCount)
        {
            firstBlock.Instructions.RemoveRange(0, argumentCount);
            
        }

        public Dictionary<Operand, List<BitArray>> BuildLiveRanges(List<BasicBlock> blocks)
        {
            InsertArgumentDefinitions(blocks);
            DoReachingDefinitionAnalysis(
                blocks,
                out Dictionary<AbstractInstruction, BitArray> Out,
                out Dictionary<AbstractInstruction, BitArray> In,
                out Dictionary<AbstractInstruction, int> defLoc);

            DoLiveVariableAnalysis(
                blocks,
                out Dictionary<AbstractInstruction, BitArray> liveIn,
                out Dictionary<AbstractInstruction, BitArray> liveOut,
                out Dictionary<Operand, int> operands);

            Dictionary<Operand, List<BitArray>> opLiveRanges = new Dictionary<Operand, List<BitArray>>(new OperandComparer());
            foreach (Operand op in operands.Keys.Where(op => op.Kind != OperandKind.Const))
            {
                opLiveRanges.Add(op, new List<BitArray>());
            }


            for (int i = 0; i < blocks.Count; i++)
            {
                BasicBlock block = blocks[i];
                for (int j = 0; j < block.Instructions.Count; j++)
                {
                    AbstractInstruction instruction = block.Instructions[j];

                    if (instruction.HasRightHandSideOperand())
                    {
                        Operand op = instruction.GetRightHandSideOperand(0);
                        BitArray liveInRange = ComputeLiveInRange(operands[op], liveIn, defLoc);
                        BitArray reachDefBeforeRange = ComputeReachingDefinitionBeforeRange(defLoc[instruction], In, defLoc);
                        BitArray newLiveRange = liveInRange.And(reachDefBeforeRange);
                        newLiveRange.Set(defLoc[instruction], true);
                        CollapseLiveRanges(opLiveRanges[op], newLiveRange);                        
                    }

                    if (instruction.Kind != InstructionKind.Call)
                    {
                        continue;
                        
                    }

                    CallInstruction call = (CallInstruction)instruction;
                    for (int k = 0; k < call.LeftHandSideOperandCount(); k++)
                    {
                        if (!call.ParametersType[k].IsByRef)
                            continue;
                        Operand implicitOp = instruction.GetLeftHandSideOperand(k);
                        BitArray liveInRange = ComputeLiveInRange(operands[implicitOp], liveIn, defLoc);
                        BitArray reachDefBeforeRange = ComputeReachingDefinitionBeforeRange(defLoc[instruction], In, defLoc);
                        BitArray newLiveRange = liveInRange.And(reachDefBeforeRange);
                        newLiveRange.Set(defLoc[instruction], true);
                        CollapseLiveRanges(opLiveRanges[implicitOp], newLiveRange);
                    }


                }
            }
            //RemoveArgumentDefinitions(blocks.First(), argCount);
            return opLiveRanges;

        }

        private void ResetOperand(List<AbstractInstruction> instructions, Operand oldOp, Operand newOp, BitArray liveRange)
        {
            for (int i = 0; i < liveRange.Count; i++)
            {
                if (!liveRange.Get(i))
                {
                    //instructions[i].SetRightHandSideOperand(0, newOp);
                    continue;
                }

                AbstractInstruction instruction = instructions[i];
                int leftCount = instruction.LeftHandSideOperandCount();
                int rightCount = instruction.RightHandSideOperandCount();

                for (int j = 0; j < leftCount; j++)
                {
                    Operand op = instruction.GetLeftHandSideOperand(j);
                    if (op.Index == oldOp.Index && op.Kind == oldOp.Kind && op.Version == oldOp.Version)
                    {
                        instruction.SetLeftHandSideOperand(j, newOp);
                    }
                }

                for (int j = 0; j < rightCount; j++)
                {
                    Operand op = instruction.GetRightHandSideOperand(j);
                    if (op.Index == oldOp.Index && op.Kind == oldOp.Kind && op.Version == oldOp.Version)
                    {
                        instruction.SetRightHandSideOperand(j, newOp);
                    }
                }
            }
        }

        private Dictionary<Operand, BitArray> ReAllocOperands(
            Dictionary<Operand, List<BitArray>> opLiveRanges,
            List<AbstractInstruction> instructions
            )
        {
            Dictionary<Operand, BitArray> newOpLiveRanges = new Dictionary<Operand, BitArray>();
            foreach (var kv in opLiveRanges)
            {
                Operand originalOp = kv.Key;
                List<BitArray> ranges = kv.Value;

                if (ranges.Count == 0)
                {
                    continue;
                }
                else if (ranges.Count == 1)
                {
                    newOpLiveRanges.Add(originalOp, ranges[0]);
                    ResetOperand(instructions, originalOp, originalOp, ranges[0]);
                }
                else
                {
                    newOpLiveRanges.Add(originalOp, ranges[0]);
                    ResetOperand(instructions, originalOp, originalOp, ranges[0]);

                    for (int i = 1; i < ranges.Count; i++)
                    {

                        Operand splitOp = new Operand(
                            originalOp.Kind,
                            originalOp.Index,
                            originalOp.OpType,
                            version: originalOp.Version + i
                        );
                        newOpLiveRanges.Add(splitOp, ranges[i]);
                        ResetOperand(instructions, originalOp, splitOp, ranges[i]);
                    }
                }
            }

            return newOpLiveRanges;


        }

        private Dictionary<Operand, List<Operand>> BuildInterferenceGraph(List<AbstractInstruction> instructions, Dictionary<Operand, List<BitArray>> opLiveRanges)
        {
            Dictionary<Operand, BitArray> newOpLiveRanges = ReAllocOperands(opLiveRanges, instructions);
            Dictionary<Operand, List<Operand>> edges = new Dictionary<Operand, List<Operand>>();

            foreach (var kv in newOpLiveRanges)
            {
                edges.Add(kv.Key, new List<Operand>());
            }

            foreach (var kv1 in newOpLiveRanges)
            {
                foreach (var kv2 in newOpLiveRanges)
                {
                    if (kv1.Key == kv2.Key)
                        continue;
                    if (BitIntersect(kv1.Value, kv2.Value))
                    {
                        edges[kv1.Key].Add(kv2.Key);
                    }
                }
            }

            return edges;
        }

        private List<ArgumentGroup> CollectFunctionArguments(List<BasicBlock> blocks)
        {
            List<ArgumentGroup> argumentGroups = new List<ArgumentGroup>();
            
            foreach (var block in blocks)
            {
                foreach (AbstractInstruction instruction in block.Instructions)
                {
                    if (instruction.Kind != InstructionKind.Call)
                    {
                        continue;
                    }
                    List<Operand> arguments = new List<Operand>();
                    int argCount = instruction.LeftHandSideOperandCount();
                    for (int i = 0; i < argCount; i++)
                    {
                        arguments.Add(instruction.GetLeftHandSideOperand(i));
                    }

                    argumentGroups.Add(new ArgumentGroup() { operands = arguments, block = block, instruction = instruction }); ;

                }
            }

            // Collect local arguemnts



            return argumentGroups;
        }

        private void AllocateRegisterForLocalArguments(List<Operand> localArguments)
        {
            int i = 0;
            foreach (Operand operand in localArguments)
            {
                operand.AssignRegister(i++);
            }
        }

        private List<Operand> CollectLocalArguments(BasicBlock firstBlock, int argCount)
        {
            //List<Operand> localArguments = new List<Operand>();
            List<Operand> localArguments = new List<Operand>();
            for (int i = 0; i < argCount; i++)
            {
                localArguments.Add(firstBlock.Instructions[i].GetRightHandSideOperand(0));
            }

            return localArguments;
        }

        private void AllocateFunctionArgumentsRegister(Dictionary<Operand, int> registerAssignment, List<ArgumentGroup> argumentGroups, Dictionary<Operand, List<Operand>> edges, bool isNewMethod)
        {
            // Try to allocate continuous arguments
            for (int i = 0; i < argumentGroups.Count; i++)
            {
                ArgumentGroup group = argumentGroups[i];
                if (group.operands.Count == 0)
                {
                    continue;
                }

                CallInstruction call = group.instruction as CallInstruction;
                if (isNewMethod != call.IsNewMethod)
                {
                    continue;
                }

                List<int> allocatedOperands = new List<int>();

                // case 1: Already allocated and is continuous
                int prevIndex = group.operands.First().Index - 1;
                bool isContinuous = true;
                for (int j = 0; j < group.operands.Count; j++)
                {
                    Operand op = group.operands[j];
                    if (op.Index - prevIndex != 1)
                    {
                        isContinuous = false;
                    }
                    prevIndex = op.Index;
                    if (op.Kind == OperandKind.Reg)
                    {
                        allocatedOperands.Add(j);
                    }
                }

                if (isContinuous && allocatedOperands.Count == group.operands.Count)
                {
                    continue;
                }

                // free all allocatedOperands
                List<MoveInstruction> moveInstructions = new List<MoveInstruction>();
                foreach (int j in allocatedOperands)
                {
                    Operand oldOperand = group.operands[j];
                    Operand newOperand = oldOperand.GetOriginalOp().Clone();
                    newOperand.Version = _ssaBuilder.GetNewVersion(oldOperand);
                    moveInstructions.Add(new MoveInstruction(AbstractOpCode.Mov, oldOperand, newOperand));
                    if (call.ParametersType[j].IsByRef)
                        _ssaBuilder.UpdateOperand(group.block, group.instruction, oldOperand, newOperand, new OperandComparer());
                    _ssaBuilder.UpdateVersion(oldOperand);
                    group.instruction.SetLeftHandSideOperand(j, newOperand);
                    group.operands[j] = newOperand;
                    // simple solution
                    foreach (KeyValuePair<Operand, List<Operand>> edge in edges)
                    {
                        edge.Value.Add(newOperand);
                    }
                }
                int instructionIndex = group.block.Instructions.IndexOf(group.instruction);
                group.block.Instructions.InsertRange(instructionIndex, moveInstructions);

                // Now alloc continuous registers for all arguments
                List<HashSet<int>> forbiddenForEachOperand = new List<HashSet<int>>();
                foreach (var op in group.operands)
                {
                    HashSet<int> forbidden = new HashSet<int>();
                    if (edges.ContainsKey(op))
                    {
                        foreach (var neighbor in edges[op])
                        {
                            if (registerAssignment.TryGetValue(neighbor, out int reg))
                            {
                                forbidden.Add(reg);
                            }
                        }
                    }                    
                    forbiddenForEachOperand.Add(forbidden);
                }

                int startReg = 0;
                bool found = false;

                while (!found)
                {
                    bool valid = true;
                    // Check if all required registers are available for this group
                    for (int j = 0; j < group.operands.Count; j++)
                    {
                        int requiredReg = startReg + j;
                        if (forbiddenForEachOperand[j].Contains(requiredReg))
                        {
                            valid = false;
                            break;
                        }
                    }

                    // Additionally, ensure none of the registers in the range are occupied by non-group operands
                    if (valid)
                    {
                        //CallInstruction call = (CallInstruction)group.instruction;
                        int opCount = call.IsNewMethod ? VirtualMachine.MAX_REGISTERS : group.operands.Count;
                        for (int j = 0; j < opCount; j++)
                        {
                            int requiredReg = startReg + j;
                            if (registerAssignment.Values.Contains(requiredReg))
                            {
                                valid = false;
                                break;
                            }
                        }
                    }

                    if (valid)
                    {
                        found = true;
                    }
                    else
                    {
                        startReg++;
                    }

                }

                // Assign registers to the group
                for (int j = 0; j < group.operands.Count; j++)
                {
                    Operand op = group.instruction.GetLeftHandSideOperand(j);
                    int reg = startReg + j;
                    registerAssignment[group.operands[j]] = reg;
                    op.AssignRegister(reg);
                }

            }

        }
        private void AllocateRegister(Dictionary<Operand, List<Operand>> edges, List<ArgumentGroup> argumentGroups, List<Operand> localArguments, List<BasicBlock> blocks)
        {
            Dictionary<Operand, int> registerAssignment = new Dictionary<Operand, int>();

            // First allocate all arguments
            // First element of argumentGroups
            
            for (int i = 0; i < localArguments.Count; i++)
            {
                int regIndex = localArguments[i].Index;
                registerAssignment.Add(localArguments[i], regIndex);
                localArguments[i].AssignRegister(regIndex);
            }

            foreach (var bb in blocks)
            {
                Console.WriteLine(bb);
            }

            AllocateFunctionArgumentsRegister(registerAssignment, argumentGroups, edges, false);

            foreach (var bb in blocks)
            {
                Console.WriteLine(bb);
            }

            // Process remaining operands using the original greedy algorithm
            List<Operand> operands = edges.Keys.Where(op => !registerAssignment.ContainsKey(op)).ToList();
            operands.Sort((op1, op2) => edges[op2].Count.CompareTo(edges[op1].Count));

            foreach (Operand operand in operands)
            {
                HashSet<int> usedRegisters = new HashSet<int>();
                foreach (Operand neighbor in edges[operand])
                {
                    if (registerAssignment.TryGetValue(neighbor, out int reg))
                    {
                        usedRegisters.Add(reg);
                    }
                }

                int register = 0;
                while (usedRegisters.Contains(register))
                {
                    register++;
                }

                registerAssignment[operand] = register;
                operand.AssignRegister(register);
            }

            AllocateFunctionArgumentsRegister(registerAssignment, argumentGroups, edges, true);


        }

    }
}
