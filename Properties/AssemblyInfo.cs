using Frosty.Core.Attributes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: ComVisible(false)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]

[assembly: Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]

[assembly: PluginDisplayName("State Machine Editor")]
[assembly: PluginAuthor("Claymaver")]
[assembly: PluginVersion("1.0.1.2")]

[assembly: RegisterAssetDefinition("CharacterStateOwnerData", typeof(StateMachineEditorPlugin.StateMachineAssetDefinition))]
