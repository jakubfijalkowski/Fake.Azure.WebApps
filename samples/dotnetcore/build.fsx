#r @"../../packages/build/FAKE/tools/FakeLib.dll"
#r @"../../build/Fake.Azure.WebApps.dll"

open System
open System.IO
open FSharp.Data

open Fake
open Fake.ZipHelper
open Fake.Azure.WebApps

let ProjectName = "dotnetcore"

let baseDir = __SOURCE_DIRECTORY__
let sourceDir = baseDir @@ "src"
let projectDir = sourceDir @@ ProjectName |> FullName
let deployDir = baseDir @@ "deploy" |> FullName
let deployZip = baseDir @@ "deploy.zip" |> FullName

let currentConfig = getBuildParamOrDefault "configuration" "Release"

let loadWebAppSettings () =
    Azure.WebApps.readSiteSettingsFromEnv (fun s ->
        { s with
            // TenantId, ClientId and ClientSecret SHOULD NOT be hard-coded here because they are considered sensitive.
            // Use env. variables (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET) on your CI server to
            // configure them.

            SubscriptionId = ""
            ResourceGroup  = ""
            WebAppName     = ""
            DeployPath     = "" })

Target "Clean" (fun () ->
    !! (projectDir @@ "bin")
    ++ (projectDir @@ "obj")
    ++ deployDir
    |> CleanDirs

    DeleteFile deployZip
)

Target "Build" (fun () ->
    trace "Restoring packages..."
    DotNetCli.Restore (fun c -> { c with WorkingDir = baseDir })

    // Running NPM, Bower, Gulp...
    trace "Building..."
    DotNetCli.Build (fun c -> { c with Configuration = currentConfig }) [projectDir]
)

Target "Publish" (fun () ->
    trace "Publishing..."
    DotNetCli.RunCommand id (sprintf "publish \"%s\" --configuration %s --output \"%s\"" projectDir currentConfig deployDir)
    trace "Zipping..."
    !! (deployDir @@ "**") |> Zip deployDir deployZip
)

Target "Upload" (fun () ->
    let settings = loadWebAppSettings ()
    let config = Azure.WebApps.acquireCredentials settings

    Azure.WebApps.stopWebAppAndWait config
    Azure.WebApps.pushZipFile config deployZip
    Azure.WebApps.startWebApp config
)

Target "Default" DoNothing

"Clean"
    ==> "Build"
    ==> "Default"
    ==> "Publish"
    ==> "Upload"

RunTargetOrDefault "Default"
