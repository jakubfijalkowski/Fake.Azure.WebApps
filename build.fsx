#r @"packages/build/FAKE/tools/FakeLib.dll"

open Fake.IO
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.DotNet.NuGet

let projectName = "Fake.Azure.WebApps"
let authors = [ "Jakub Fijałkowski" ]

let rootDir = "."
let srcDir = Path.combine rootDir "src"
let buildDir = Path.combine rootDir "build"
let projectDir = Path.combine srcDir projectName

let release = ReleaseNotes.LoadReleaseNotes "CHANGELOG.md"

let additionalFiles =
    [ "LICENSE.txt"
      "README.md"
      "CHANGELOG.md" ]

Target.Create "Clean" (fun _ ->
    Shell.CleanDirs
        [ buildDir
          Path.combine projectDir "bin"
          Path.combine projectDir "obj"]
)

Target.Create "SetAssemblyInfo" (fun _ ->
    let infoFile = Path.combine projectDir "AssemblyInfo.fs"

    [ AssemblyInfo.Product "FAKE - Azure WebApps helper"
      AssemblyInfo.Version release.AssemblyVersion
      AssemblyInfo.InformationalVersion release.AssemblyVersion
      AssemblyInfo.FileVersion release.AssemblyVersion
      AssemblyInfo.Title "FAKE - Azure WebApps helper"
      AssemblyInfo.Guid "d683a57b-a955-4307-8319-8dae6e710825" ]
    |> AssemblyInfoFile.CreateFSharp infoFile
)

Target.Create "Build" (fun _ ->
    MsBuild.MSBuildRelease buildDir "Build" [ projectName + ".sln" ]
    |> Trace.Log "AppBuild-Output:"
)

Target.Create "CreateNuGet" (fun _ ->
    let nuspecFile = projectName + ".nuspec"
    let refFile = Path.combine projectDir "paket.references"
    let deps = Paket.GetDependenciesForReferencesFile refFile |> Array.toList
    NuGet.NuGet (fun s ->
        { s with
            Authors = authors
            Project = projectName
            Summary = "Simple FAKE helper that makes deploying Azure WebApps a breeze"
            Description = "This package provides FAKE helpers that allows to reliably publish your app to Azure WebApps using just a FAKE scripts."
            Version = release.NugetVersion
            OutputPath = buildDir
            WorkingDir = rootDir
            ReleaseNotes = release.Notes |> String.toLines
            Publish = false
            Dependencies = deps
            Files = [ (@"build/Fake.Azure.WebApps.dll", Some "lib/net451", None) ] })
        nuspecFile
)

Target.Create "PublishNuGet" (fun _ ->
    Paket.Push (fun p -> { p with WorkingDir = buildDir })
)

Target.Create "Default" Target.DoNothing

"Clean"
    ==> "SetAssemblyInfo"
    ==> "Build"
    ==> "Default"
    ==> "CreateNuGet"
    ==> "PublishNuGet"

Target.RunOrDefault "Default"
