﻿using System;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyFactories;

public class SoftObjectFactory
{
    public static SoftObjectPropertyData Create(string value, string substring = "")
    {
        if (FactoryUtils.ASSET == null)
            throw new Exception("FactoryUtils.ASSET is null. Did you forget to set it?");
        
        SoftObjectPropertyData data = new();

        FactoryUtils.ASSET.AddNameReference(new FString(value));

        int PackageIndex = FactoryUtils.ASSET.SearchNameReference(new FString(value.Split('.')[0]));
        int AssetIndex = value.Contains('.') ? FactoryUtils.ASSET.SearchNameReference(new FString(value.Split('.')[1])) : -1;

        FSoftObjectPath Value = new()
        {
            AssetPath = new FTopLevelAssetPath(new FName(FactoryUtils.ASSET, PackageIndex), new FName(FactoryUtils.ASSET, AssetIndex)),
            SubPathString = new FString(substring)
        };

        data.Value = Value;
        return data;
    }
}