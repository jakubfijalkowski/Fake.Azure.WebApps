#r @"../../packages/build/FAKE/tools/FakeLib.dll"
#r @"../../build/Fake.Azure.WebApps.dll"

open System
open System.IO
open FSharp.Data

open Fake.Azure
open Fake.IO
open Fake.IO.Zip
open Fake.Core
open Fake.Core.Environment
open Fake.Core.TargetOperators
open Fake.DotNet.Cli

let ProjectName = "dotnetcore"

let baseDir = __SOURCE_DIRECTORY__
let sourceDir = Path.combine baseDir "src"
let projectDir = Path.combine sourceDir ProjectName |> Path.GetFullPath
let deployDir = Path.combine baseDir "deploy" |> Path.GetFullPath
let deployZip = Path.combine baseDir "deploy.zip" |> Path.GetFullPath

let currentConfig =
    environVarOrDefault "DOTNET_CONFIGURATION" "Release" |> BuildConfiguration.Custom

if Fake.EnvironmentHelper.isUnix then
    DefaultDotnetCliDir <- "/usr/bin"

let loadWebAppSettings () =
    WebApps.readSiteSettingsFromEnv (fun s ->
        { s with
            // TenantId, ClientId and ClientSecret SHOULD NOT be hard-coded here because they are considered sensitive.
            // Use env. variables (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET) on your CI server to
            // configure them.

            SubscriptionId = ""
            ResourceGroup  = ""
            WebAppName     = ""
            // This is the default path to the site, you can configure this on the Application Settings blade
            DeployPath     = "site/wwwroot" })

Target.Create "Clean" (fun _ ->
    Shell.CleanDirs
        [ deployDir
          Path.combine projectDir "bin"
          Path.combine projectDir "obj"]

    File.delete deployZip
)

Target.Create "Build" (fun _ ->
    Trace.traceVerbose "Restoring packages..."
    DotnetRestore id projectDir

    // Running NPM, Bower, Gulp...
    Trace.traceVerbose "Building..."
    DotnetCompile (fun c -> { c with Configuration = currentConfig }) projectDir
)

Target.Create "Publish" (fun _ ->
    Trace.traceVerbose "Publishing..."
    DotnetPublish
        (fun c ->
            { c with
                Configuration = currentConfig
                OutputPath = Some deployDir }) 
        projectDir
    Trace.traceVerbose "Zipping..."
    Zip deployDir deployZip [ Path.combine deployDir "**" ]
)

Target.Create "Upload" (fun _ ->
    let settings = loadWebAppSettings ()
    let config = WebApps.acquireCredentials settings

    WebApps.stopDotNetCoreAppAndWait config
    WebApps.pushZipFile config deployZip
    WebApps.startWebApp config
)

Target.Create "Default" Target.DoNothing

"Clean"
    ==> "Build"
    ==> "Default"
    ==> "Publish"
    ==> "Upload"

Target.RunOrDefault "Default"
