namespace System
open System.Reflection
open System.Runtime.InteropServices

[<assembly: AssemblyProductAttribute("FAKE - Azure WebApps helper")>]
[<assembly: AssemblyVersionAttribute("0.2.0")>]
[<assembly: AssemblyInformationalVersionAttribute("0.2.0")>]
[<assembly: AssemblyFileVersionAttribute("0.2.0")>]
[<assembly: AssemblyTitleAttribute("FAKE - Azure WebApps helper")>]
[<assembly: GuidAttribute("d683a57b-a955-4307-8319-8dae6e710825")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.2.0"
    let [<Literal>] InformationalVersion = "0.2.0"
