﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Regulus.Core.Ssa.Instruction
{
    //public enum ValueOperandType
    //{
    //    Integer,
    //    Long,
    //    Float,
    //    Double,
    //    Null,
    //    Object
    //}
    public class ValueOperand : Operand
    {
        private byte[] _value;
        private static List<string> s_internStrings = new List<string>();
        private static Dictionary<int, ValueOperandType> s_localTypes = new Dictionary<int, ValueOperandType>();

        public static string[] GetInternedStrings()
        {
            return s_internStrings.ToArray();
        }

        public ValueOperand(OperandKind kind, int index, int version, bool isFixed, byte[] value, ValueOperandType type) : base(kind, index, type, version, isFixed)
        {
            _value = value;
        }
        public ValueOperand(OperandKind kind, int index, ValueOperandType type) : base(kind, index, type)
        {
            _value = new byte[1];
       
        }

        public ValueOperand(OperandKind kind, int index, int value, ValueOperandType type, ValueOperandType localType) : base(kind, index, type)
        {
            //_value = BitConverter.GetBytes(value);
            if (!s_localTypes.ContainsKey(value))
            {
                s_localTypes.Add(value, localType);
            }

            _value = BitConverter.GetBytes(value);
        }


        public ValueOperand(OperandKind type, int index, int value) : base(type, index, ValueOperandType.Integer)
        {
            _value = BitConverter.GetBytes(value);
            //ValueType = ValueOperandType.Integer;

        }

        public ValueOperand(OperandKind type, int index, long value) : base(type, index, ValueOperandType.Long)
        {
            _value = BitConverter.GetBytes(value);
            //ValueType = ValueOperandType.Long;
            
        }

        public ValueOperand(OperandKind type, int index, float value) : base(type, index, ValueOperandType.Float)
        {
            
            _value = BitConverter.GetBytes(value);
            //ValueType = ValueOperandType.Float;
            
        }

        public unsafe ValueOperand(OperandKind type, int index, double value) : base(type, index, ValueOperandType.Double)
        {
            _value = BitConverter.GetBytes(value);
            //ValueType = ValueOperandType.Double;
            
        }

        public ValueOperand(OperandKind type, int index, string str) : base(type, index, ValueOperandType.String)
        {
            int i = s_internStrings.IndexOf(str);
            if (i == -1)
            {
                i = s_internStrings.Count;
                s_internStrings.Add(str);
                
            }

            _value = BitConverter.GetBytes(i);
        }

        public ValueOperand(OperandKind type, int index) : base(type, index, ValueOperandType.Null)
        {
            _value = new byte[1];
            //ValueType = ValueOperandType.Null;
           

        }

        public byte[] GetValue()
        {
            return _value;
        }

        public int GetInt()
        {
            return BitConverter.ToInt32(_value);
        }

        public long GetLong()
        {
            return BitConverter.ToInt64(_value);
        }

        public float GetFloat()
        {
            return BitConverter.ToSingle(_value);
        }

        public double GetDouble()
        {
            return BitConverter.ToDouble(_value);
        }

        public int GetStringIndex()
        {
            return BitConverter.ToInt32(_value);
        }

        public void Neg()
        {
            if (OpType == ValueOperandType.Null ||
                OpType == ValueOperandType.Object)
                throw new InvalidOperationException();

            if (OpType == ValueOperandType.Integer)
            {
                int value = BitConverter.ToInt32(_value);
                _value = BitConverter.GetBytes(-value);
                return;
            }

            if (OpType == ValueOperandType.Long)
            {
                long value = BitConverter.ToInt64(_value);
                _value = BitConverter.GetBytes(-value);
                return;
            }
                

            if (OpType == ValueOperandType.Float)
            {
                float value = BitConverter.ToSingle(_value);
                _value = BitConverter.GetBytes(-value);
                return;
            }

            if (OpType == ValueOperandType.Double)
            {
                double value = BitConverter.ToDouble(_value);
                _value = BitConverter.GetBytes(-value);
                return;
            }
            
        }

        public override unsafe Operand Clone()
        {
            return new ValueOperand(Kind, Index, Version, IsFixed, _value, OpType);
        }

        public Operand ResolveLocalPointer()
        {
            if (OpType != ValueOperandType.LocalPointer && OpType != ValueOperandType.ArgPointer)
            {
                throw new InvalidOperationException("Only localpointer can be resolved");
            }
            if (OpType == ValueOperandType.LocalPointer)
                return new Operand(OperandKind.Local, GetInt(), s_localTypes[GetInt()]);
            else
                return new Operand(OperandKind.Arg, GetInt(), s_localTypes[GetInt()]);
            
        }

        private string ValueToString()
        {
            if (Kind == OperandKind.Const)
            {
                switch(OpType)
                {
                    case ValueOperandType.Null:
                        return "[Null]";
                    case ValueOperandType.Integer:
                        return $"[{GetInt()}]";
                    case ValueOperandType.Long:
                        return $"[{GetLong()}]";
                    case ValueOperandType.Float:
                        return $"[{GetFloat()}]";
                    case ValueOperandType.Double:
                        return $"[{GetDouble()}]";
                    case ValueOperandType.String:
                        return $"[{s_internStrings[GetStringIndex()]}]";
                    case ValueOperandType.LocalPointer:
                        return $"[&{GetInt()}]";
                    case ValueOperandType.ArgPointer:
                        return $"[&{GetInt()}]";
                    //case ValueOperandType.Reference:
                    //    return $"[Ref:{GetInt()}]";
                    default:
                        throw new NotImplementedException();
                }
            }
            else
            {
                return "";
            }
                
        }
        
        public override string ToString()
        {
            //switch (OpType)
            //{
            //    case ValueOperandType.Integer:
            //        return Kind == OperandKind.Const ?
            //            $"{base.ToString()} [{_value}:Int]" : $"{base.ToString()} [Int]";
            //    case ValueOperandType.Long:
            //        return Kind == OperandKind.Const ?
            //            $"{base.ToString()} [{_value}:Long]" : $"{base.ToString()} [Long]";
            //    case ValueOperandType.Float:
            //        return Kind == OperandKind.Const ?
            //            $"{base.ToString()} [{_value}:Float]" : $"{base.ToString()} [Float]";
            //    case ValueOperandType.Double:
            //        return Kind == OperandKind.Const ?
            //            $"{base.ToString()} [{_value}:Double]" : $"{base.ToString()} [Double]";
            //}
            
            return $"{base.ToString()}{ValueToString()}";
        }

        public override bool IsDefault()
        {
            return base.IsDefault() && Kind != OperandKind.Const;
        }


    }
}
