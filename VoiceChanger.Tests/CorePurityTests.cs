using System.Reflection;
using VoiceChanger.Core;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class CorePurityTests
{
    private static readonly HashSet<string> AllowedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "mscorlib",
        "netstandard"
    };

    [Fact]
    public void Core_HasZeroExternalDependencies_OnlyReferencesBcl()
    {
        Assembly coreAssembly = typeof(IAudioProcessor).Assembly;
        AssemblyName[] referencedAssemblies = coreAssembly.GetReferencedAssemblies();

        foreach (AssemblyName refName in referencedAssemblies)
        {
            string name = refName.Name ?? string.Empty;
            bool isAllowed = AllowedPrefixes.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase) || 
                                                      name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

            Assert.True(isAllowed, 
                $"Invariant #3 Violation: VoiceChanger.Core references non-BCL assembly '{name}'. Core must be pure managed DSP over Spans with zero external dependencies.");
        }
    }
}
