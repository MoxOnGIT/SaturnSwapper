﻿using System.Collections.Generic;
using Saturn.Backend.Data.SaturnAPI.Models;
using Saturn.Backend.Data.Swapper.Assets;

namespace Saturn.Backend.Data.Swapper.Swapping.Models;

public class PresetModel
{
    public string PresetName { get; set; }
    public List<Swaps> PresetSwaps { get; set; } = new();
}

public class Swaps
{
    public AssetSelectorItem OptionModel { get; set; }
    public AssetSelectorItem ItemModel { get; set; }
}