﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Saturn.Backend.Data.Fortnite;
using Saturn.Backend.Data.FortniteCentral;
using Saturn.Backend.Data.FortniteCentral.Models;
using Saturn.Backend.Data.Variables;

namespace Saturn.Backend.Data.Swapper;

public class SaturnFileProvider : IDisposable
{
    public DefaultFileProvider Provider => _provider;
    private readonly DefaultFileProvider _provider;
    
    public SaturnFileProvider(IFortniteCentralService fortniteCentralService)
    {
        _provider = new DefaultFileProvider(DataCollection.GetGamePath(), SearchOption.TopDirectoryOnly, true,
            new VersionContainer(EGame.GAME_UE5_4));
        
        _provider.Initialize();

        if (!Directory.EnumerateFiles(Constants.MappingsPath).Any())
            throw new Exception("No mappings found in the mappings folder!");
        
        // Get the newest file in the mappings folder
        var mappingFile = Directory.EnumerateFiles(Constants.MappingsPath).OrderByDescending(x => x).First();
        
        // Load the mappings
        _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingFile);
        
        // Load the aes
        var fetchedAes = fortniteCentralService.ReturnEndpoint<FortniteCentralAESModel>("/api/v1/aes");
        
        for (int i = 0; i < 10; i++)
        {
            if (fetchedAes == null)
                fetchedAes = fortniteCentralService.ReturnEndpoint<FortniteCentralAESModel>("/api/v1/aes");
            else
                break;
        }

        if (fetchedAes == null)
        {
            Logger.Log("AES was null!");
            fetchedAes = new FortniteCentralAESModel()
            {
                MainKey = null,
                DynamicKeys = new()
            };
        }
        
        // Submit the main aes key
        _provider.SubmitKey(new FGuid(),
            fetchedAes.MainKey != null
                ? new FAesKey(fetchedAes.MainKey)
                : new FAesKey("0x0000000000000000000000000000000000000000000000000000000000000000"));

        // Submit the dynamic aes keys
        var dynamicAesKeys = fetchedAes.DynamicKeys.Select(aes => new KeyValuePair<FGuid, FAesKey>(new FGuid(aes.Guid), new FAesKey(aes.Key))).ToList();
        _provider.SubmitKeys(dynamicAesKeys);

        Constants.CanSpecialSwap = _provider.MountedVfs.Any(x => x.Name == "pakchunk0-WindowsClient.pak");

        GlobalFileProvider.Provider = _provider;
    }
    
    public void Dispose()
    {
        _provider.Dispose();
        _provider.UnloadAllVfs();
    }
}