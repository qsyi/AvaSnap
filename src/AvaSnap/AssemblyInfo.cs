using System.Runtime.CompilerServices;
using System.Windows;

// Lets the scratchpad GpuVerify/GpuProfile harnesses call the Gpu*.cs
// services' internal texture-in/texture-out methods (ApplyToTexture,
// BlendIntoTexture, ...) directly -- used for regression/equivalence
// testing and performance profiling of the GPU effect pipeline. Neither
// harness project is part of this repo/build.
[assembly: InternalsVisibleTo("GpuVerify")]
[assembly: InternalsVisibleTo("GpuProfile")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
