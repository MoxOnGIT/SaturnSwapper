﻿using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;

namespace UAssetAPI.PropertyTypes.Structs
{
    public class BoxPropertyData : PropertyData<VectorPropertyData[]> // Min, Max, IsValid
    {
        [JsonProperty]
        public bool IsValid;

        public BoxPropertyData(FName name) : base(name)
        {

        }

        public BoxPropertyData()
        {

        }

        private static readonly FString CurrentPropertyType = new FString("Box");
        public override bool HasCustomStructSerialization { get { return true; } }
        public override FString PropertyType { get { return CurrentPropertyType; } }

        public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0)
        {
            if (includeHeader)
            {
                PropertyGuid = reader.ReadPropertyGuid();
            }

            Value = new VectorPropertyData[2];
            for (int i = 0; i < 2; i++)
            {
                var next = new VectorPropertyData(Name);
                next.Ancestry.Initialize(Ancestry, Name);
                next.Read(reader, false, 0);
                Value[i] = next;
            }

            IsValid = reader.ReadBoolean();
        }

        public override void ResolveAncestries(UnrealPackage asset, AncestryInfo ancestrySoFar)
        {
            var ancestryNew = (AncestryInfo)ancestrySoFar.Clone();
            ancestryNew.SetAsParent(Name);

            foreach (var entry in Value) entry.ResolveAncestries(asset, ancestryNew);
            base.ResolveAncestries(asset, ancestrySoFar);
        }
        
        public override byte[] Serialize(UnrealPackage Asset)
        {
            List<byte> data = new();
            
            for (int i = 0; i < 2; i++)
            {
                data.AddRange(Value[i].Serialize(Asset));
            }
            data.AddRange(BitConverter.GetBytes(IsValid));

            return data.ToArray();
        }

        public override int Write(AssetBinaryWriter writer, bool includeHeader)
        {
            if (includeHeader)
            {
                writer.WritePropertyGuid(PropertyGuid);
            }

            int totalSize = 0;
            for (int i = 0; i < 2; i++)
            {
                totalSize += Value[i].Write(writer, includeHeader);
            }
            writer.Write(IsValid);
            return totalSize + sizeof(bool);
        }

        public override void FromString(string[] d, UAsset asset)
        {
            IsValid = d[0].Equals("1") || d[0].ToLower().Equals("true");
        }

        public override string ToString()
        {
            string oup = "(";
            for (int i = 0; i < Value.Length; i++)
            {
                oup += Value[i] + ", ";
            }
            return oup.Remove(oup.Length - 2) + ")";
        }

        protected override void HandleCloned(PropertyData res)
        {
            BoxPropertyData cloningProperty = (BoxPropertyData)res;

            VectorPropertyData[] newData = new VectorPropertyData[this.Value.Length];
            for (int i = 0; i < this.Value.Length; i++)
            {
                newData[i] = (VectorPropertyData)this.Value[i].Clone();
            }
            cloningProperty.Value = newData;
        }
    }
}