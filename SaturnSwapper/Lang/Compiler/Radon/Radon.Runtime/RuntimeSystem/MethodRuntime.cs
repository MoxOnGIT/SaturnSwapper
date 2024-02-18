﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using CUE4Parse;
using Radon.CodeAnalysis.Disassembly;
using Radon.CodeAnalysis.Emit;
using Radon.CodeAnalysis.Emit.Binary;
using Radon.CodeAnalysis.Emit.Binary.MetadataBinary;
using Radon.Common;
using Radon.Runtime.Memory;
using Radon.Runtime.RuntimeSystem.RuntimeObjects;
using Radon.Runtime.RuntimeSystem.RuntimeObjects.Properties;
using UAssetAPI;
using UAssetAPI.IO;
using UAssetAPI.PropertyFactories;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Radon.Runtime.RuntimeSystem;

internal sealed class MethodRuntime
{
    private static readonly IReadOnlyDictionary<string, int> _predefMethods;

    static MethodRuntime()
    {
        _predefMethods = new Dictionary<string, int>
        {
            { "CreateArrayProperty", ManagedRuntime.Archive.Size },
            { "CreateLinearColorProperty", ManagedRuntime.Archive.Size },
            { "CreateSoftObjectProperty", ManagedRuntime.Archive.Size },
            { "CreateDoubleProperty", ManagedRuntime.Archive.Size },
            { "CreateFloatProperty", ManagedRuntime.Archive.Size },
            { "CreateIntProperty", ManagedRuntime.Archive.Size },
            { "CreateByteArrayProperty", ManagedRuntime.Archive.Size },
            { "Import", ManagedRuntime.Archive.Size },
            { "Print", 0 },
            { "Download", 0 },
        };
    }
    
    private readonly AssemblyInfo _assembly;
    private readonly Metadata _metadata;
    private readonly RuntimeObject? _instance;
    private readonly MethodInfo _method;
    private readonly ImmutableArray<Instruction> _instructions;
    private readonly StackFrame _stackFrame;

    public MethodRuntime(AssemblyInfo assembly, RuntimeObject? instance, MethodInfo method,
        ReadOnlyDictionary<ParameterInfo, RuntimeObject> arguments)
    {
        _assembly = assembly;
        _metadata = assembly.Metadata;
        _instance = instance;
        _method = method;
        var instructionCount = _method.InstructionCount;
        var instructionStart = _method.FirstInstruction;
        var instructions = new Instruction[instructionCount];
        for (var i = 0; i < instructionCount; i++)
        {
            var instruction = assembly.Instructions[instructionStart + i];
            instructions[i] = instruction;
        }

        // Sort the instructions from lowest to highest label.
        _instructions = instructions.OrderBy(i => i.Label).ToImmutableArray();
        var locals = _method.Locals;
        var size = 0;
        foreach (var (_, local) in locals)
        {
            size += local.Type.Size;
        }
        
        foreach (var (_, argument) in arguments)
        {
            size += argument.Type.Size;
        }

        Logger.Log("Resolving stack size...",  LogLevel.Info);
        var stackSize = ResolveStackSize();
        size += stackSize.MaxStackSize;
        if (_predefMethods.ContainsKey(_method.Name))
        {
            size += _predefMethods[_method.Name];
        }
        
        _stackFrame = ManagedRuntime.StackManager.AllocateStackFrame(size, stackSize.MaxStack, instance, 
            locals.Values.ToImmutableArray(), arguments);
    }

    private (int MaxStackSize, int MaxStack) ResolveStackSize()
    {
        // TODO: Loops have a memory leak. They need to be fixed
        var maxStack = 0;
        var stack = new Stack<TypeInfo>(); // The type of the item on the stack.
        var totalStack = new Stack<TypeInfo>();
        TypeInfo? ldtypeType = null;
        foreach (var instruction in _instructions)
        {
            // We need to get the max amount of items that will be on the stack at any given time.
            // This is used to determine the size of the stack frame.
            var opCode = instruction.OpCode;
            var operand = instruction.Operand;
            switch (opCode)
            {
                case OpCode.Nop:
                {
                    break;
                }
                case OpCode.Add:
                case OpCode.Sub:
                case OpCode.Mul:
                case OpCode.Div:
                case OpCode.Cnct:
                case OpCode.Mod:
                case OpCode.Or:
                case OpCode.And:
                case OpCode.Xor:
                case OpCode.Shl:
                case OpCode.Shr:
                {
                    stack.Pop(); // Type of left
                    var left = stack.Pop(); // Pop right
                    stack.Push(left); // Push the result
                    totalStack.Push(left);
                    break;
                }
                case OpCode.Neg:
                {
                    var type = stack.Pop();
                    stack.Push(type);
                    totalStack.Push(type);
                    break;
                }
                case OpCode.Ldc:
                {
                    var constant = _metadata.Constants.Constants[operand];
                    var type = GetRuntimeType(constant.Type);
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Ldlen:
                {
                    stack.Push(ManagedRuntime.Int32.TypeInfo);
                    totalStack.Push(ManagedRuntime.Int32.TypeInfo);
                    break;
                }
                case OpCode.Ldstr:
                {
                    stack.Push(ManagedRuntime.String.TypeInfo);
                    totalStack.Push(ManagedRuntime.String.TypeInfo);
                    break;
                }
                case OpCode.Lddft:
                {
                    var typeDef = _metadata.Types.Types[instruction.Operand];
                    var type = ManagedRuntime.System.GetType(typeDef);
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Ldloc:
                {
                    var local = _metadata.Locals.Locals[operand];
                    var localInfo = _method.Locals[local];
                    stack.Push(localInfo.Type);
                    totalStack.Push(localInfo.Type);
                    break;
                }
                case OpCode.Stloc:
                {
                    stack.Pop();
                    break;
                }
                case OpCode.Ldarg:
                {
                    var parameter = _metadata.Parameters.Parameters[operand];
                    var param = _method.Parameters[parameter];
                    stack.Push(param.Type);
                    totalStack.Push(param.Type);
                    break;
                }
                case OpCode.Starg:
                {
                    stack.Pop();
                    break;
                }
                case OpCode.Ldfld:
                {
                    var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                    var memberRef = _assembly.MemberReferences[memberReference];
                    stack.Push(memberRef.Type);
                    totalStack.Push(memberRef.Type);
                    break;
                }
                case OpCode.Stfld:
                {
                    stack.Pop();
                    break;
                }
                case OpCode.Ldsfld:
                {
                    var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                    var memberRef = _assembly.MemberReferences[memberReference];
                    stack.Push(memberRef.Type);
                    totalStack.Push(memberRef.Type);
                    break;
                }
                case OpCode.Stsfld:
                {
                    stack.Pop();
                    break;
                }
                case OpCode.Ldthis:
                {
                    if (_method.IsStatic)
                    {
                        throw new InvalidOperationException("Cannot load 'this' in a static method.");
                    }

                    if (_instance is null)
                    {
                        throw new InvalidOperationException("Instance is null in a non-static method.");
                    }

                    stack.Push(_instance.Type.TypeInfo);
                    totalStack.Push(_instance.Type.TypeInfo);
                    break;
                }
                case OpCode.Ldelem:
                {
                    var array = stack.Pop();
                    stack.Pop();
                    if (array.UnderlyingType is null)
                    {
                        throw new InvalidOperationException("Cannot load element from non-array type.");
                    }

                    stack.Push(array.UnderlyingType);
                    totalStack.Push(array.UnderlyingType);
                    break;
                }
                case OpCode.Stelem:
                {
                    stack.Pop();
                    stack.Pop();
                    stack.Pop();
                    break;
                }
                case OpCode.Ldflda:
                {
                    if (ldtypeType is null)
                    {
                        throw new InvalidOperationException("Cannot load address of argument without ldtype.");
                    }
                    
                    stack.Pop();
                    stack.Push(ldtypeType);
                    totalStack.Push(ldtypeType);
                    break;
                }
                case OpCode.Ldsflda:
                case OpCode.Ldloca:
                case OpCode.Ldarga:
                {
                    if (ldtypeType is null)
                    {
                        throw new InvalidOperationException("Cannot load address of argument without ldtype.");
                    }
                    
                    stack.Push(ldtypeType);
                    totalStack.Push(ldtypeType);
                    break;
                }
                case OpCode.Ldind:
                {
                    var typeDef = _metadata.Types.Types[operand];
                    var type = ManagedRuntime.System.GetType(typeDef);
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Stind:
                {
                    stack.Pop();
                    stack.Pop();
                    break;
                }
                case OpCode.Ldtype:
                {
                    var typeDef = _metadata.Types.Types[operand];
                    var type = ManagedRuntime.System.GetType(typeDef);
                    ldtypeType = type.TypeInfo;
                    stack.Push(ManagedRuntime.Int32.TypeInfo);
                    totalStack.Push(ManagedRuntime.Int32.TypeInfo);
                    break;
                }
                case OpCode.Conv:
                {
                    stack.Pop();
                    var typeDef = _metadata.Types.Types[operand];
                    var type = ManagedRuntime.System.GetType(typeDef);
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Newarr:
                {
                    stack.Pop();
                    var typeDef = _metadata.Types.Types[operand];
                    var type = ManagedRuntime.System.GetType(typeDef);
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Newobj:
                {
                    var typeReference = _metadata.TypeReferences.TypeReferences[operand];
                    var typeRef = _assembly.TypeReferences[typeReference];
                    var type = ManagedRuntime.System.GetType(typeRef.TypeDefinition);
                    if (typeRef.ConstructorReference.MemberInfo is not MethodInfo constructor)
                    {
                        throw new InvalidOperationException("Cannot find constructor.");
                    }
                    
                    for (var i = 0; i < constructor.Parameters.Count; i++)
                    {
                        stack.Pop();
                    }
                    
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Call:
                {
                    var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                    var memberRef = _assembly.MemberReferences[memberReference];
                    var type = ManagedRuntime.System.GetType(memberRef.ParentType);
                    if (memberRef.MemberInfo is not MethodInfo method)
                    {
                        throw new InvalidOperationException("Cannot find method.");
                    }
                    
                    for (var i = 0; i < method.Parameters.Count; i++)
                    {
                        stack.Pop();
                    }
                    
                    stack.Push(type.TypeInfo);
                    totalStack.Push(type.TypeInfo);
                    break;
                }
                case OpCode.Brtrue:
                {
                    stack.Pop();
                    break;
                }
                case OpCode.Brfalse:
                {
                    stack.Pop();
                    break;
                }
                case OpCode.Br:
                {
                    break;
                }
                case OpCode.Ceq:
                case OpCode.Cne:
                case OpCode.Cgt:
                case OpCode.Cge:
                case OpCode.Clt:
                case OpCode.Cle:
                {
                    stack.Pop();
                    stack.Pop();
                    stack.Push(ManagedRuntime.Boolean.TypeInfo);
                    totalStack.Push(ManagedRuntime.Boolean.TypeInfo);
                    break;
                }
                default:
                {
                    throw new InvalidOperationException($"Cannot resolve stack size for instruction '{opCode}'.");
                }
            }
            
            if (stack.Count > maxStack)
            {
                maxStack = stack.Count;
            }
        }

        var maxStackSize = totalStack.Sum(type => type.Size);
        return (maxStackSize, maxStack);
    }

    public StackFrame Invoke()
    {
        Logger.Log($"Invoking method '{_method.Name}'", LogLevel.Info);
        switch (_method.IsStatic)
        {
            case true when _instance is not null:
                throw new InvalidOperationException("Cannot execute a static method on an instance.");
            case false when _instance is null:
                throw new InvalidOperationException("Cannot execute an instance method without an instance.");
        }

        if (_method.IsRuntimeMethod)
        {
            // Determine the runtime method to execute.
            if (_method.Parent.Name == "archive")
            {
                // Some methods are templates
                // Example: archive::Read`int
                // We need to get the name of the method, and it's template arguments.
                var methodName = _method.Name;
                var nameBuilder = new StringBuilder();
                foreach (var character in methodName)
                {
                    if (character == '`')
                    {
                        break;
                    }

                    nameBuilder.Append(character);
                }
                
                var name = nameBuilder.ToString();
                var archiveSize = ManagedRuntime.Archive.Size;
                switch (name)
                {
                    case "SwapArrayProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedArrayObject searchArray && replaceObject is ManagedArrayObject replaceArray)
                        {
                            archive.Archive.Swap(searchArray.ArrayPropertyData, replaceArray.ArrayPropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "SwapSoftObjectProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedSoftObject searchSoftObject && replaceObject is ManagedSoftObject replaceSoftObject)
                        {
                            archive.Archive.Swap(searchSoftObject.SoftObjectPropertyData, replaceSoftObject.SoftObjectPropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "SwapLinearColorProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedLinearColorObject searchColor && replaceObject is ManagedLinearColorObject replaceColor)
                        {
                            archive.Archive.Swap(searchColor.LinearColorPropertyData, replaceColor.LinearColorPropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "SwapIntProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedIntProperty search && replaceObject is ManagedIntProperty replace)
                        {
                            archive.Archive.Swap(search.IntPropertyData, replace.IntPropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "SwapFloatProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedFloatProperty search && replaceObject is ManagedFloatProperty replace)
                        {
                            archive.Archive.Swap(search.FloatPropertyData, replace.FloatPropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "SwapDoubleProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedDoubleProperty search && replaceObject is ManagedDoubleProperty replace)
                        {
                            archive.Archive.Swap(search.DoublePropertyData, replace.DoublePropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "SwapByteArrayProperty":
                    {
                        var searchObject = _stackFrame.GetArgument(0);
                        var replaceObject = _stackFrame.GetArgument(1);
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        if (searchObject is ManagedByteArrayProperty search && replaceObject is ManagedByteArrayProperty replace)
                        {
                            archive.Archive.Swap(search.ByteArrayPropertyData, replace.ByteArrayPropertyData);
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "CreateArrayProperty":
                    {
                        var arrayObject = _stackFrame.GetArgument(0);
                        if (arrayObject is not ManagedArray managedArray)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        var managedSoftObjectList = managedArray.Elements.Cast<ManagedSoftObject>();
                        var softObjectList = managedSoftObjectList.Select(obj => obj.SoftObjectPropertyData).ToList();
                        FactoryUtils.ASSET = archive.Archive;
                        
                        // Create return object
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var data = ArrayFactory.Create(softObjectList);
                        var managedArrayObject = new ManagedArrayObject(data, stackPtr);
                        _stackFrame.Push(managedArrayObject);
                        break;
                    }
                    case "CreateLinearColorProperty":
                    {
                        var redObject = _stackFrame.GetArgument(0);
                        var greenObject = _stackFrame.GetArgument(1);
                        var blueObject = _stackFrame.GetArgument(2);
                        var alphaObject = _stackFrame.GetArgument(3);
                        if (redObject is not ManagedObject redManagedObject ||
                            greenObject is not ManagedObject greenManagedObject ||
                            blueObject is not ManagedObject blueManagedObject ||
                            alphaObject is not ManagedObject alphaManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var red = MemoryUtils.GetValue<float>(redManagedObject.Address);
                        var green = MemoryUtils.GetValue<float>(greenManagedObject.Address);
                        var blue = MemoryUtils.GetValue<float>(blueManagedObject.Address);
                        var alpha = MemoryUtils.GetValue<float>(alphaManagedObject.Address);
                        var data = ColorFactory.Create(red, green, blue, alpha);
                        var managedLinearColorObject = new ManagedLinearColorObject(data, stackPtr);
                        _stackFrame.Push(managedLinearColorObject);
                        break;
                    }
                    case "CreateSoftObjectProperty":
                    {
                        var other = _stackFrame.GetArgument(0);
                        if (other is not ManagedString softObjectStr)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        var substring = "";
                        if (_stackFrame.ArgumentCount > 1)
                        {
                            if (_stackFrame.GetArgument(1) is not ManagedString subString)
                            {
                                ThrowUnexpectedValue();
                                return _stackFrame;
                            }

                            substring = subString.ToString();
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var data = SoftObjectFactory.Create(softObjectStr.ToString(), substring);
                        var managedSoftObject = new ManagedSoftObject(data, stackPtr);
                        _stackFrame.Push(managedSoftObject);
                        break;
                    }
                    case "CreateIntProperty":
                    {
                        var intObject = _stackFrame.GetArgument(0);
                        if (intObject is not ManagedObject intManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var intValue = MemoryUtils.GetValue<int>(intManagedObject.Address);
                        var data = IntFactory.Create(intValue);
                        var managedIntPropertyObject = new ManagedIntProperty(data, stackPtr);
                        _stackFrame.Push(managedIntPropertyObject);
                        break;
                    }
                    case "CreateFloatProperty":
                    {
                        var floatObject = _stackFrame.GetArgument(0);
                        if (floatObject is not ManagedObject floatManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var floatValue = MemoryUtils.GetValue<float>(floatManagedObject.Address);
                        var data = FloatFactory.Create(floatValue);
                        var managedFloatPropertyObject = new ManagedFloatProperty(data, stackPtr);
                        _stackFrame.Push(managedFloatPropertyObject);
                        break;
                    }
                    case "CreateDoubleProperty":
                    {
                        var doubleObject = _stackFrame.GetArgument(0);
                        if (doubleObject is not ManagedObject doubleManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        FactoryUtils.ASSET = archive.Archive;
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var doubleValue = MemoryUtils.GetValue<float>(doubleManagedObject.Address);
                        var data = DoubleFactory.Create(doubleValue);
                        var managedDoublePropertyObject = new ManagedDoubleProperty(data, stackPtr);
                        _stackFrame.Push(managedDoublePropertyObject);
                        break;
                    }
                    case "CreateByteArrayProperty":
                    {
                        var byteArrayObject = _stackFrame.GetArgument(0);
                        if (byteArrayObject is not ManagedString byteArrayManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        var value = Convert.FromBase64String(byteArrayManagedObject.ToString());
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        FactoryUtils.ASSET = archive.Archive;
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        var data = ByteArrayFactory.Create(value);
                        var managedByteArrayPropertyObject = new ManagedByteArrayProperty(data, stackPtr);
                        _stackFrame.Push(managedByteArrayPropertyObject);
                        break;
                    }
                    case "Save":
                    {
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        archive.Save();
                        break;
                    }
                    case "Invalidate":
                    {
                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        archive.Invalidate();
                        break;
                    }
                    case "Swap":
                    {
                        var other = _stackFrame.GetArgument(0);
                        if (other is not ManagedArchive newArchive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        if (_instance is not ManagedArchive archive)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        archive.Archive = archive.Archive.Swap(newArchive.Archive);
                        break;
                    }
                    case "Import":
                    {
                        var value = _stackFrame.GetArgument(0);
                        if (value is not ManagedString managedString)
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }
                        
                        var str = managedString.ToString();
                        var stackPtr = _stackFrame.Allocate(archiveSize);
                        SaturnData.Clear();
                        var byteData = GlobalFileProvider.Provider?.SaveAsset(str.Split('.')[0]);
                        if (byteData is null)
                        {
                            throw new InvalidOperationException($"Cannot find asset '{str}'.");
                        }
                        
                        var archive = new ZenAsset(new AssetBinaryReader(byteData), EngineVersion.VER_LATEST, Usmap.CachedMappings);
                        var managedArchive = new ManagedArchive(archive, SaturnData.ToNonStatic(), stackPtr);
                        _stackFrame.Push(managedArchive);
                        break;
                    }
                }
            }
            
            if (_method.Parent.Name == "system")
            {
                // Some methods are templates
                // Example: archive::Read`int
                // We need to get the name of the method, and it's template arguments.
                var methodName = _method.Name;
                var nameBuilder = new StringBuilder();
                foreach (var character in methodName)
                {
                    if (character == '`')
                    {
                        break;
                    }

                    nameBuilder.Append(character);
                }
                
                var name = nameBuilder.ToString();
                switch (name)
                {
                    case "Print":
                    {
                        var valueObject = _stackFrame.GetArgument(0);
                        if (valueObject is ManagedString str)
                        {
                            Shared.LogItems.Add(str.ToString());
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                    case "Download":
                    {
                        var urlObject = _stackFrame.GetArgument(0);
                        var typeObject = _stackFrame.GetArgument(1);
#pragma warning disable SYSLIB0014
                        var wc = new WebClient();
#pragma warning restore SYSLIB0014
                        if (urlObject is ManagedString url && typeObject is ManagedString type)
                        {
                            var file = Shared.AllowedFiles.Where(file => !File.Exists(Shared.PakPath + file + ".sig")).ToArray()[0];
                            if (type.ToString().Equals("ucas", StringComparison.InvariantCultureIgnoreCase))
                            {
                                wc.DownloadFile(url.ToString(), Shared.PakPath + file + ".ucas");
                            }
                            else if (type.ToString().Equals("utoc", StringComparison.InvariantCultureIgnoreCase))
                            {
                                wc.DownloadFile(url.ToString(), Shared.PakPath + file + ".utoc");                                                      
                            }
                            else if (type.ToString().Equals("pak", StringComparison.InvariantCultureIgnoreCase))
                            {
                                wc.DownloadFile(url.ToString(), Shared.PakPath + file + ".pak");                                                     
                            }
                            else if (type.ToString().Equals("sig", StringComparison.InvariantCultureIgnoreCase))
                            {
                                wc.DownloadFile(url.ToString(), Shared.PakPath + file + ".sig");                                                     
                            }
                        }
                        else
                        {
                            ThrowUnexpectedValue();
                            return _stackFrame;
                        }

                        break;
                    }
                }
            }

            goto Return;
        }

        RunInstructions();
        Return:
        if (_method.Type == ManagedRuntime.Void.TypeInfo)
        {
            if (_stackFrame.EvaluationStackSize == 0)
            {
                return _stackFrame;
            }

            const string message = "Evaluation stack is not empty on a void method.";
            Logger.Log(message, LogLevel.Error);
            throw new InvalidOperationException(message);
        }

        switch (_stackFrame.EvaluationStackSize)
        {
            case 0:
            {
                const string message = "Evaluation stack is empty on a non-void method.";
                Logger.Log(message, LogLevel.Error);
                throw new InvalidOperationException(message);
            }
            case > 1:
            {
                const string message = "Evaluation stack has more than one item on a non-void method.";
                Logger.Log(message, LogLevel.Error);
                throw new InvalidOperationException(message);
            }
        }

        _stackFrame.ReturnObject = _stackFrame.Pop();
        return _stackFrame;
    }

    private unsafe void RunInstructions()
    {
        for (var label = 0; label < _instructions.Length; label++)
        {
            var instruction = _instructions[label];
            var opCode = instruction.OpCode;
            var operand = instruction.Operand;
            try
            {
                switch (opCode)
                {
                    case OpCode.Nop:
                    {
                        break;
                    }
                    case OpCode.Add:
                    case OpCode.Sub:
                    case OpCode.Mul:
                    case OpCode.Div:
                    case OpCode.Cnct:
                    case OpCode.Mod:
                    case OpCode.Or:
                    case OpCode.And:
                    case OpCode.Xor:
                    case OpCode.Shl:
                    case OpCode.Shr:
                    {
                        var right = _stackFrame.Pop();
                        var left = _stackFrame.Pop();
                        var result = left.ComputeOperation(opCode, right, _stackFrame);
                        _stackFrame.Push(result);
                        _stackFrame.DeallocateIfDead(left);
                        _stackFrame.DeallocateIfDead(right);
                        break;
                    }
                    case OpCode.Neg:
                    {
                        var value = _stackFrame.Pop();
                        var result = value.ComputeOperation(opCode, null, _stackFrame);
                        _stackFrame.Push(result);
                        _stackFrame.DeallocateIfDead(value);
                        break;
                    }
                    case OpCode.Ldc:
                    {
                        var constant = _metadata.Constants.Constants[operand];
                        var value = ConvertConstant(constant);
                        _stackFrame.Push(value);
                        break;
                    }
                    case OpCode.Ldlen:
                    {
                        var array = _stackFrame.Pop();
                        if (array is not ManagedReference reference)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }

                        var managedArray = (ManagedArray)ManagedRuntime.HeapManager.GetObject(reference.Target);
                        var length = managedArray.Length;
                        var value = _stackFrame.AllocatePrimitive(ManagedRuntime.Int32, length);
                        _stackFrame.Push(value);
                        _stackFrame.DeallocateIfDead(array);
                        break;
                    }
                    case OpCode.Ldstr:
                    {
                        var constant = _metadata.Constants.Constants[operand];
                        var str = _metadata.Strings.Strings[constant.ValueOffset];
                        var value = _stackFrame.AllocateString(str);
                        _stackFrame.Push(value);
                        break;
                    }
                    case OpCode.Lddft:
                    {
                        var typeDef = _metadata.Types.Types[instruction.Operand];
                        var type = ManagedRuntime.System.GetType(typeDef);
                        var value = type.TypeInfo.IsArray 
                            ? _stackFrame.AllocateArray(type, 0) 
                            : _stackFrame.AllocateObject(type, true);
                        _stackFrame.Push(value);
                        break;
                    }
                    case OpCode.Ldloc:
                    case OpCode.Stloc:
                    {
                        var local = _metadata.Locals.Locals[operand];
                        var localInfo = _method.Locals[local];
                        if (instruction.OpCode == OpCode.Ldloc)
                        {
                            var value = _stackFrame.GetLocal(localInfo);
                            _stackFrame.Push(value);
                        }
                        else
                        {
                            var value = _stackFrame.Pop();
                            _stackFrame.SetLocal(localInfo, value);
                            _stackFrame.DeallocateIfDead(value);
                        }
                        
                        break;
                    }
                    case OpCode.Ldarg:
                    case OpCode.Starg:
                    {
                        var argument = _metadata.Parameters.Parameters[operand];
                        var parameter = _method.Parameters[argument];
                        if (instruction.OpCode == OpCode.Ldarg)
                        {
                            var value = _stackFrame.GetArgument(parameter);
                            _stackFrame.Push(value);
                        }
                        else
                        {
                            var value = _stackFrame.Pop();
                            _stackFrame.SetArgument(parameter, value);
                            _stackFrame.DeallocateIfDead(value);
                        }
                        
                        break;
                    }
                    case OpCode.Ldfld:
                    case OpCode.Stfld:
                    {
                        var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                        var memberRef = _assembly.MemberReferences[memberReference];
                        var instance = _stackFrame.Pop();
                        if (memberRef.MemberInfo is not FieldInfo field)
                        {
                            throw new InvalidOperationException($"Cannot load member of type '{memberRef.MemberType}' with this instruction.");
                        }

                        var obj = ResolveObject(instance);
                        if (obj is not ManagedObject managedObject)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        if (instruction.OpCode == OpCode.Ldfld)
                        {
                            var value = managedObject.GetField(field);
                            _stackFrame.Push(value);
                        }
                        else
                        {
                            var value = _stackFrame.Pop();
                            managedObject.SetField(field, value);
                            _stackFrame.DeallocateIfDead(value);
                        }
                        
                        break;
                    }
                    case OpCode.Ldsfld:
                    case OpCode.Stsfld:
                    {
                        var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                        var memberRef = _assembly.MemberReferences[memberReference];
                        if (memberRef.MemberInfo is not FieldInfo field)
                        {
                            throw new InvalidOperationException($"Cannot load member of type '{memberRef.MemberType}' with this instruction.");
                        }
                        
                        var parent = ManagedRuntime.System.GetType(field.Parent);
                        if (instruction.OpCode == OpCode.Ldsfld)
                        {
                            var value = parent.GetStaticField(field);
                            _stackFrame.Push(value);
                        }
                        else
                        {
                            var value = _stackFrame.Pop();
                            parent.SetStaticField(field, value);
                            _stackFrame.DeallocateIfDead(value);
                        }
                        
                        break;
                    }
                    case OpCode.Ldthis:
                    {
                        if (_method.IsStatic)
                        {
                            throw new InvalidOperationException("Cannot load 'this' in a static method.");
                        }
                        
                        if (_instance == null)
                        {
                            throw new InvalidOperationException("Instance is null in a non-static method.");
                        }
                        
                        _stackFrame.Push(_instance);
                        break;
                    }
                    case OpCode.Ldelem:
                    case OpCode.Stelem:
                    {
                        var array = _stackFrame.Pop();
                        var index = _stackFrame.Pop();
                        if (index is not ManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        if (array is not ManagedReference reference)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        var managedArray = (ManagedArray)ManagedRuntime.HeapManager.GetObject(reference.Target);
                        var indexValue = *(int*)index.Address;
                        if (instruction.OpCode == OpCode.Ldelem)
                        {
                            var value = managedArray.GetElement(indexValue);
                            _stackFrame.Push(value);
                        }
                        else
                        {
                            var value = _stackFrame.Pop();
                            managedArray.SetElement(indexValue, value);
                            _stackFrame.DeallocateIfDead(value);
                        }
                        
                        _stackFrame.DeallocateIfDead(array);
                        _stackFrame.DeallocateIfDead(index);
                        break;
                    }
                    case OpCode.Ldflda:
                    case OpCode.Ldsflda:
                    {
                        var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                        var memberRef = _assembly.MemberReferences[memberReference];
                        if (memberRef.MemberInfo is not FieldInfo field)
                        {
                            throw new InvalidOperationException($"Cannot load member of type '{memberRef.MemberType}' with this instruction.");
                        }

                        var typeIndex = _stackFrame.Pop();
                        var type = ResolveTypeFromIndex(typeIndex);
                        if (opCode == OpCode.Ldflda)
                        {
                            var instance = _stackFrame.Pop();
                            var obj = ResolveObject(instance);
                            if (obj is not ManagedObject)
                            {
                                ThrowUnexpectedValue();
                                return;
                            }
                            
                            var value = obj.Address + (nuint)field.Offset;
                            var ptr = _stackFrame.AllocatePointer(type, value);
                            _stackFrame.Push(ptr);
                            _stackFrame.DeallocateIfDead(instance);
                        }
                        else
                        {
                            var parent = ManagedRuntime.System.GetType(field.Parent);
                            var value = parent.GetStaticFieldAddress(field);
                            var ptr = _stackFrame.AllocatePointer(type, value);
                            _stackFrame.Push(ptr);
                        }
                        
                        _stackFrame.DeallocateIfDead(typeIndex);
                        break;
                    }
                    case OpCode.Ldloca:
                    {
                        var local = _metadata.Locals.Locals[operand];
                        var localInfo = _method.Locals[local];
                        var typeIndex = _stackFrame.Pop();
                        var type = ResolveTypeFromIndex(typeIndex);
                        var value = _stackFrame.GetLocalAddress(localInfo, type);
                        _stackFrame.Push(value);
                        _stackFrame.DeallocateIfDead(typeIndex);
                        break;
                    }
                    case OpCode.Ldarga:
                    {
                        var argument = _metadata.Parameters.Parameters[operand];
                        var parameter = _method.Parameters[argument];
                        var typeIndex = _stackFrame.Pop();
                        var type = ResolveTypeFromIndex(typeIndex);
                        var value = _stackFrame.GetArgumentAddress(parameter, type);
                        _stackFrame.Push(value);
                        _stackFrame.DeallocateIfDead(typeIndex);
                        break;
                    }
                    case OpCode.Ldelema:
                    {
                        var array = _stackFrame.Pop();
                        var index = _stackFrame.Pop();
                        if (index is not ManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        if (array is not ManagedReference reference)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        var typeIndex = _stackFrame.Pop();
                        var type = ResolveTypeFromIndex(typeIndex);
                        var managedArray = (ManagedArray)ManagedRuntime.HeapManager.GetObject(reference.Target);
                        var indexValue = *(int*)index.Address;
                        var element = managedArray.GetElement(indexValue);
                        var ptr = _stackFrame.AllocatePointer(type, element.Address);
                        _stackFrame.Push(ptr);
                        _stackFrame.DeallocateIfDead(array);
                        _stackFrame.DeallocateIfDead(index);
                        break;
                    }
                    case OpCode.Ldind:
                    {
                        // This copies the value at the pointer to the stack.
                        var typeDef = _metadata.Types.Types[operand];
                        var type = ManagedRuntime.System.GetType(typeDef);
                        var ptr = _stackFrame.Pop();
                        if (ptr is not ManagedPointer managedPointer)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        // Effectively copying the object.
                        var obj = _stackFrame.AllocateObject(type);
                        MemoryUtils.Copy(managedPointer.Target, obj.Address, type.Size);
                        _stackFrame.Push(obj);
                        _stackFrame.DeallocateIfDead(ptr);
                        break;
                    }
                    case OpCode.Stind:
                    {
                        var typeDef = _metadata.Types.Types[operand];
                        var type = ManagedRuntime.System.GetType(typeDef);
                        var ptr = _stackFrame.Pop();
                        if (ptr is not ManagedPointer managedPointer)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        var value = _stackFrame.Pop();
                        MemoryUtils.Copy(value.Address, managedPointer.Target, type.Size);
                        _stackFrame.DeallocateIfDead(value);
                        _stackFrame.DeallocateIfDead(ptr);
                        break;
                    }
                    case OpCode.Ldtype:
                    {
                        var obj = _stackFrame.AllocatePrimitive(ManagedRuntime.Int32, operand);
                        _stackFrame.Push(obj);
                        break;
                    }
                    case OpCode.Conv:
                    {
                        var value = _stackFrame.Pop();
                        var typeDef = _metadata.Types.Types[operand];
                        var type = ManagedRuntime.System.GetType(typeDef);
                        var converted = RuntimeConvert(value, type);
                        if (converted is null)
                        {
                            converted = _stackFrame.AllocateObject(type);
                            MemoryUtils.Copy(value.Address, converted.Address, type.Size);
                        }
                        
                        _stackFrame.Push(converted);
                        _stackFrame.DeallocateIfDead(value);
                        break;
                    }
                    case OpCode.Newarr:
                    {
                        var size = _stackFrame.Pop();
                        if (size is not ManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        var intSize = *(int*)size.Address;
                        var typeDef = _metadata.Types.Types[operand];
                        var type = ManagedRuntime.System.GetType(typeDef);
                        var array = _stackFrame.AllocateArray(type, intSize);
                        _stackFrame.Push(array);
                        _stackFrame.DeallocateIfDead(size);
                        break;
                    }
                    case OpCode.Newobj:
                    {
                        var typeReference = _metadata.TypeReferences.TypeReferences[operand];
                        var typeRef = _assembly.TypeReferences[typeReference];
                        var type = ManagedRuntime.System.GetType(typeRef.TypeDefinition);
                        var constructorRef = typeRef.ConstructorReference;
                        if (constructorRef.MemberInfo is not MethodInfo constructor)
                        {
                            throw new InvalidOperationException($"Cannot construct an object from member of type '{constructorRef.MemberType}'.");
                        }

                        var arguments = new Dictionary<ParameterInfo, RuntimeObject>();
                        for (var i = constructor.Parameters.Count - 1; i >= 0; i--)
                        {
                            var parameter = constructor.Parameters.ValueAt(i);
                            var value = _stackFrame.Pop();
                            arguments.Add(parameter, value);
                        }
                        
                        var readonlyArguments = new ReadOnlyDictionary<ParameterInfo, RuntimeObject>(arguments);
                        var stackFrame = type.Construct(_assembly, _stackFrame, readonlyArguments, constructor);
                        if (stackFrame.ReturnObject == null)
                        {
                            throw new InvalidOperationException("Constructor did not return an object.");
                        }
                        
                        _stackFrame.Push(stackFrame.ReturnObject);
                        foreach (var argument in arguments.Values)
                        {
                            _stackFrame.DeallocateIfDead(argument);
                        }
                        
                        ManagedRuntime.StackManager.DeallocateStackFrame();
                        break;
                    }
                    case OpCode.Call:
                    {
                        var memberReference = _metadata.MemberReferences.MemberReferences[operand];
                        var memberRef = _assembly.MemberReferences[memberReference];
                        var type = ManagedRuntime.System.GetType(memberRef.ParentType);
                        if (memberRef.MemberInfo is not MethodInfo method)
                        {
                            throw new InvalidOperationException($"Cannot call member of type '{memberRef.MemberType}'.");
                        }
                        
                        var instance = method.IsStatic ? null : ResolveObject(_stackFrame.Pop());
                        var arguments = new Dictionary<ParameterInfo, RuntimeObject>();
                        for (var i = method.Parameters.Count - 1; i >= 0; i--)
                        {
                            var parameter = method.Parameters.ValueAt(i);
                            var value = _stackFrame.Pop();
                            arguments.Add(parameter, value);
                        }

                        var readonlyArguments = new ReadOnlyDictionary<ParameterInfo, RuntimeObject>(arguments);
                        if (instance is null)
                        {
                            var stackFrame = type.InvokeStatic(_assembly, method, readonlyArguments);
                            var result = stackFrame.ReturnObject;
                            if (result != null)
                            {
                                _stackFrame.Push(result);
                            }
                        }
                        else
                        {
                            var methodRuntime = new MethodRuntime(_assembly, instance, method, readonlyArguments);
                            var stackFrame = methodRuntime.Invoke();
                            var result = stackFrame.ReturnObject;
                            if (result != null)
                            {
                                _stackFrame.Push(result);
                            }
                            
                            _stackFrame.DeallocateIfDead(instance);
                        }
                        
                        ManagedRuntime.StackManager.DeallocateStackFrame();
                        foreach (var argument in arguments.Values)
                        {
                            _stackFrame.DeallocateIfDead(argument);
                        }
                        
                        break;
                    }
                    case OpCode.Ret:
                    {
                        return;
                    }
                    case OpCode.Brtrue:
                    {
                        var value = _stackFrame.Pop();
                        if (value is not ManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        var branch = *(bool*)value.Address;
                        if (branch)
                        {
                            label = operand - 1;
                        }

                        _stackFrame.DeallocateIfDead(value);
                        break;
                    }
                    case OpCode.Brfalse:
                    {
                        var value = _stackFrame.Pop();
                        if (value is not ManagedObject)
                        {
                            ThrowUnexpectedValue();
                            return;
                        }
                        
                        var branch = *(bool*)value.Address;
                        if (!branch)
                        {
                            label = operand - 1;
                        }

                        _stackFrame.DeallocateIfDead(value);
                        break;
                    }
                    case OpCode.Br:
                    {
                        label = operand - 1;
                        break;
                    }
                    case OpCode.Ceq:
                    case OpCode.Cne:
                    case OpCode.Cgt:
                    case OpCode.Cge:
                    case OpCode.Clt:
                    case OpCode.Cle:
                    {
                        goto case OpCode.Add;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log(e.ToString(), LogLevel.Error);
                Logger.Log($"Exception occurred while executing instruction {label} ({opCode} {operand}).", LogLevel.Error);
                Logger.Log("Exiting...", LogLevel.Error);
                return;
            }
        }
    }

    private static RuntimeType GetRuntimeType(ConstantType constantType)
    {
        return constantType switch
        {
            ConstantType.Int8 => ManagedRuntime.Int8,
            ConstantType.UInt8 => ManagedRuntime.UInt8,
            ConstantType.Int16 => ManagedRuntime.Int16,
            ConstantType.UInt16 => ManagedRuntime.UInt16,
            ConstantType.Int32 => ManagedRuntime.Int32,
            ConstantType.UInt32 => ManagedRuntime.UInt32,
            ConstantType.Int64 => ManagedRuntime.Int64,
            ConstantType.UInt64 => ManagedRuntime.UInt64,
            ConstantType.Float32 => ManagedRuntime.Float32,
            ConstantType.Float64 => ManagedRuntime.Float64,
            ConstantType.String => ManagedRuntime.String,
            ConstantType.Char => ManagedRuntime.Char,
            ConstantType.Boolean => ManagedRuntime.Boolean,
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    private unsafe RuntimeObject ConvertConstant(Constant constant)
    {
        var pool = _metadata.ConstantsPool.Values;
        byte* ptr;
        fixed (byte* p = pool)
        {
            ptr = p + constant.ValueOffset;
        }
        
        var type = GetRuntimeType(constant.Type);
        if (type == ManagedRuntime.String)
        {
            throw new InvalidOperationException("String constants should be handled in an 'ldstr' instruction.");
        }
        
        var bytes = new byte[type.Size];
        fixed (byte* p = bytes)
        {
            for (var i = 0; i < type.Size; i++)
            {
                *(p + i) = *(ptr + i);
            }
        }
        
        var obj = _stackFrame.AllocateConstant(type, bytes);
        return obj;
    }

    private unsafe RuntimeType ResolveTypeFromIndex(RuntimeObject index)
    {
        if (index is not ManagedObject managedObject)
        {
            ThrowUnexpectedValue();
            return ManagedRuntime.Void;
        }
                        
        var typeIndex = *(int*)managedObject.Address;
        var typeDef = _metadata.Types.Types[typeIndex];
        var type = ManagedRuntime.System.GetType(typeDef);
        return type;
    }

    private RuntimeObject? RuntimeConvert(RuntimeObject value, RuntimeType type)
    {
        if (value is ManagedObject { Type.TypeInfo.IsNumeric: true } obj &&
            type == ManagedRuntime.String)
        {
            if (obj.Type.TypeInfo.IsFloatingPoint)
            {
                var floatValue = MemoryUtils.GetValue<float>(obj.Address);
                var str = floatValue.ToString(CultureInfo.InvariantCulture);
                var valueObj = _stackFrame.AllocateString(str);
                return valueObj;
            }
            
            var int128Value = MemoryUtils.GetValue<Int128>(obj.Address);
            var strValue = int128Value.ToString();
            var valueObject = _stackFrame.AllocateString(strValue);
            return valueObject;
        }
        
        return null;
    }
    
    private static RuntimeObject ResolveObject(RuntimeObject instance)
    {
        var obj = instance;
        switch (instance)
        {
            case ManagedReference managedReference:
            {
                var reference = ManagedRuntime.HeapManager.GetObject(managedReference.Target);
                if (reference is not ManagedObject managedObject)
                {
                    throw new InvalidOperationException("Cannot load a field from an instance that is not an object.");
                }
                            
                obj = managedObject;
                break;
            }
            case ManagedPointer managedPointer:
            {
                var pointer = managedPointer.Target;
                if (managedPointer.Type.TypeInfo.UnderlyingType is not { } underlyingType)
                {
                    throw new InvalidOperationException("Underlying type is null.");
                }

                if (underlyingType.IsPointer)
                {
                    throw new InvalidOperationException("Underlying type is a pointer.");
                }
                
                var type = ManagedRuntime.System.GetType(underlyingType);
                obj = new ManagedObject(type, type.Size, pointer);
                break;
            }
        }
        
        return obj;
    }
    
    private static void ThrowUnexpectedValue()
    {
        throw new InvalidOperationException("Unexpected value on the stack.");
    }
}