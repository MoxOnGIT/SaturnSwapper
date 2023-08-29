﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Radon.CodeAnalysis.Emit;
using Radon.Common;
using Radon.Runtime.Memory;

namespace Radon.Runtime.RuntimeSystem.RuntimeObjects;

internal sealed class ManagedArray : RuntimeObject
{
    private readonly List<RuntimeObject> _elements;
    public override RuntimeType Type { get; }
    public override int Size { get; } // The size in bytes of the array on the heap. This includes the 4 bytes for the length.
    public override UIntPtr Pointer { get; } // The address of the array on the heap.
    public RuntimeType ElementType { get; }
    public nuint ArrayStart => Pointer + sizeof(int);
    public int Length { get; }
    public ImmutableArray<RuntimeObject> Elements => _elements.ToImmutableArray();

    public ManagedArray(RuntimeType type, nuint pointer, IEnumerable<RuntimeObject> elements)
    {
        _elements = new List<RuntimeObject>(elements);
        Type = type;
        if (type.TypeInfo.UnderlyingType is null)
        {
            throw new InvalidOperationException("Underlying type cannot be null.");
        }

        ElementType = ManagedRuntime.System.GetType(type.TypeInfo.UnderlyingType);
        Size = ElementType.Size * _elements.Count;
        Pointer = pointer;
        Length = _elements.Count;
    }
    
    public RuntimeObject GetElement(int index)
    {
        Logger.Log($"Getting element at index {index}.", LogLevel.Info);
        return _elements[index];
    }
    
    public void SetElement(int index, RuntimeObject element)
    {
        Logger.Log($"Setting element at index {index}.", LogLevel.Info);
        _elements[index] = element;
        var pointer = ArrayStart + (nuint)index * (nuint)ElementType.Size;
        MemoryUtils.Copy(element.Pointer, pointer, ElementType.Size);
    }
    
    public override RuntimeObject ComputeOperation(OpCode operation, RuntimeObject? other, StackFrame stackFrame)
    {
        if (other is not ManagedArray otherArray)
        {
            throw new InvalidOperationException("Cannot perform an operation on an array and a non-array.");
        }
        
        switch (operation)
        {
            case OpCode.Ceq:
            {
                return stackFrame.AllocatePrimitive(ManagedRuntime.Boolean, Pointer == otherArray.Pointer);
            }
            case OpCode.Cne:
            {
                return stackFrame.AllocatePrimitive(ManagedRuntime.Boolean, Pointer != otherArray.Pointer);
            }
        }
        
        throw new InvalidOperationException($"Cannot perform operation {operation} on an array.");
    }
    
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(ElementType);
        sb.Append('[');
        sb.Append(Length);
        sb.Append(']');
        sb.Append(' ');
        sb.Append("{ ");
        for (var i = 0; i < Length; i++)
        {
            sb.Append(_elements[i]);
            if (i != Length - 1)
            {
                sb.Append(", ");
            }
        }
        
        sb.Append(" }");
        return sb.ToString();
    }
}