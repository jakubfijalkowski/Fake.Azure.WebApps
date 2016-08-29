#I @"packages/build/FAKE/tools/"
#r @"FakeLib.dll"

open Fake
open Fake.Git
open Fake.Paket
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper

let projectName = "Fake.Azure.WebApps"

let rootDir = "."
let srcDir = rootDir @@ "src"
let buildDir = rootDir @@ "build"
let projectDir = srcDir @@ projectName
let projectBuildOutput = projectDir @@ "bin/Release"

let nugetDir = rootDir @@ "nuget"
let nugetLibDir = nugetDir @@ "lib"
let nugetLib451Dir = nugetLibDir @@ "net451"

let release = LoadReleaseNotes "CHANGELOG.md"

let authors = [ "Jakub Fijałkowski" ]
let additionalFiles =
    [ "LICENSE.txt"
      "README.md"
      "CHANGELOG.md" ]

let flip f a b = f b a

Target "Clean" (fun () ->
    CleanDirs
        [ buildDir
          nugetDir
          projectDir @@ "bin"
          projectDir @@ "obj"]
)

Target "SetAssemblyInfo" (fun () ->
    let infoFile = srcDir @@ projectName @@ "AssemblyInfo.fs"

    [ Attribute.Product "FAKE - Azure WebApps helper"
      Attribute.Version release.AssemblyVersion
      Attribute.InformationalVersion release.AssemblyVersion
      Attribute.FileVersion release.AssemblyVersion
      Attribute.Title "FAKE - Azure WebApps helper"
      Attribute.Guid "d683a57b-a955-4307-8319-8dae6e710825" ]
    |> CreateFSharpAssemblyInfo infoFile
)

Target "Build" (fun () ->
    MSBuildRelease buildDir "Build" [ projectName + ".sln" ] |> Log "AppBuild-Output:"
)

Target "CopyToOutput" (fun () ->
    CopyTo nugetLib451Dir [ buildDir @@ (projectName + ".dll") ]
    CopyTo nugetDir additionalFiles
)

Target "CreateNuGet" (fun () ->
    let nuspecFile = projectName + ".nuspec"
    let deps = GetDependenciesForReferencesFile (projectDir @@ "paket.references") |> Array.toList
    flip NuGet nuspecFile (fun s ->
        { s with
            Authors = authors
            Project = projectName
            Summary = "Simple FAKE helper that makes deploying Azure WebApps a breeze"
            Description = "This package provides FAKE helpers that allows to reliably publish your app to Azure WebApps using just a FAKE scripts."
            Version = release.NugetVersion
            OutputPath = nugetDir
            ReleaseNotes = release.Notes |> toLines
            Publish = false
            Dependencies = deps })
)

Target "PublishNuGet" (fun () ->
    Paket.Push (fun p -> { p with WorkingDir = nugetDir })
)

Target "Release" (fun () ->
    let tagName = "v" + release.NugetVersion

    StageAll ""
    Commit "" <| sprintf "Bump version to %s" release.NugetVersion
    Branches.tag "" tagName

    Branches.push ""
    Branches.pushTag "" "origin" tagName
)

Target "Default" DoNothing

"Clean"
    ==> "SetAssemblyInfo"
    ==> "Build"
    ==> "Default"
    ==> "CopyToOutput"
    ==> "CreateNuGet"
    ==> "PublishNuGet"
    ==> "Release"

RunTargetOrDefault "Default"
