using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using R2API.Utils;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class PluginAttributeTests
{
    [Fact]
    public void TestProjectIsReady()
    {
        Assert.Equal(4, 2 + 2);
    }

    [Fact]
    public void PluginClassIsMarkedNoNeedForSync()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "RoR2LatencyMod.dll"));
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var pluginTypes = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Where(type => type.GetCustomAttributes()
                .Any(handle => GetAttributeTypeName(metadata, handle) == "BepInEx.BepInPlugin"))
            .ToArray();

        Assert.Single(pluginTypes);

        var compatibilityAttributes = pluginTypes[0].GetCustomAttributes()
            .Where(handle => GetAttributeTypeName(metadata, handle) == "R2API.Utils.NetworkCompatibility")
            .ToArray();

        Assert.Single(compatibilityAttributes);

        var blob = metadata.GetBlobReader(metadata.GetCustomAttribute(compatibilityAttributes[0]).Value);
        Assert.Equal(0x0001, blob.ReadUInt16());
        Assert.Equal((int)CompatibilityLevel.NoNeedForSync, blob.ReadInt32());
    }

    private static string GetAttributeTypeName(MetadataReader metadata, CustomAttributeHandle handle)
    {
        var constructor = metadata.GetCustomAttribute(handle).Constructor;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)constructor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                    return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
                }

                break;
            case HandleKind.MethodDefinition:
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor);
                var declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
                return metadata.GetString(declaringType.Namespace) + "." + metadata.GetString(declaringType.Name);
        }

        return string.Empty;
    }
}
