module Fake.Azure.WebApps

open System
open System.Text
open System.IO
open System.Net
open FSharp.Data
open Fake

[<Literal>]
let private AzurePublishProfile = "FTP"

let private tokenEndpoint : Printf.StringFormat<_> = "https://login.microsoftonline.com/%s/oauth2/token"
let private siteEndpoint : Printf.StringFormat<_> = "https://management.azure.com/subscriptions/%s/resourcegroups/%s/providers/Microsoft.Web/sites/%s/%s?api-version=2016-03-01"
let private scmEndpoint : Printf.StringFormat<_> = "https://%s.scm.azurewebsites.net/api/%s"
let private siteAddress : Printf.StringFormat<_> = "https://%s.azurewebsites.net"

type private AzureTokenResponse = JsonProvider<"""{"token_type":"","expires_in":"","ext_expires_in":"","expires_on":"","not_before":"","resource":"","access_token":""}""">
type private AzurePublishXmlResponse = XmlProvider<"""<publishData><publishProfile publishMethod="" userName="" userPWD="" /><publishProfile publishMethod="" userName="" userPWD="" /></publishData>""">
type private CommandResponse = JsonProvider<"""{"Output":"test","Error":"test","ExitCode":0}""">

type AzureWebAppSettings = {
    TenantId       : string
    ClientId       : string
    ClientSecret   : string
    SubscriptionId : string
    ResourceGroup  : string
    WebAppName     : string
    DeployPath     : string
}

type AzureWebAppCredentials = {
    AccessToken    : string
    DeployUserName : string
    DeployPassword : string
}

type AzureConfiguration = {
    Settings    : AzureWebAppSettings
    Credentials : AzureWebAppCredentials
}

type CommandExecResult = {
    Output   : string
    Error    : string
    ExitCode : int
}
let private makeBearerHeader = (+) "Bearer " >> HttpRequestHeaders.Authorization
let private makeBasicAuthHeader cred =
    sprintf "%s:%s" cred.DeployUserName cred.DeployPassword
    |> Encoding.UTF8.GetBytes
    |> Convert.ToBase64String
    |> (+) "Basic "
    |> HttpRequestHeaders.Authorization

let private getSiteStatus settings =
    let url = sprintf siteAddress settings.WebAppName
    let request = HttpWebRequest.CreateHttp(url)
    request.Method <- "HEAD"
    let response =
        try
            request.GetResponse() :?> HttpWebResponse
        with
            | :? WebException as ex ->
                match ex.Response with
                | null -> reraise ()
                | b    -> b :?> HttpWebResponse
    int response.StatusCode, response.StatusDescription

let private makeEmptyRequest url httpMethod credentials =
    let headers = [ makeBearerHeader credentials.AccessToken ]
    if httpMethod = HttpMethod.Get
    then Http.RequestString (url, httpMethod = httpMethod, headers = headers)
    else Http.RequestString (url, httpMethod = httpMethod, headers = headers, body = BinaryUpload [||])

let private callWebAppEndpoint settings credentials httpMethod action =
    traceVerbose <| "Calling WebApp action " + action
    let url = sprintf siteEndpoint settings.SubscriptionId settings.ResourceGroup settings.WebAppName action
    let response = makeEmptyRequest url httpMethod credentials
    traceVerbose <| sprintf "Action %s on WebApp %s finished successfully" action settings.WebAppName
    response

let private callWebAppSCMEndpoint settings credentials httpMethod action =
    traceVerbose <| "Calling Kudu action " + action
    let url = sprintf scmEndpoint settings.WebAppName action
    let headers = [ makeBasicAuthHeader credentials ]
    let response =
        if action = HttpMethod.Get
        then Http.RequestString (url, httpMethod = httpMethod, headers = headers)
        else Http.RequestString (url, httpMethod = httpMethod, headers = headers, body = BinaryUpload [||])
    traceVerbose <| sprintf "Action %s on Kudu for WebApp %s finished successfully" action settings.WebAppName
    response

let private acquireAccessToken settings =
    let url = sprintf tokenEndpoint settings.TenantId
    let response =
        Http.RequestString (url, body = FormValues
            [ "resource",      "https://management.azure.com/";
              "grant_type",    "client_credentials";
              "client_id",     settings.ClientId;
              "client_secret", settings.ClientSecret ])
    let json = response |> AzureTokenResponse.Parse
    traceVerbose "Access token acquired successfully"
    { AccessToken = json.AccessToken.JsonValue.AsString ();
      DeployUserName = ""
      DeployPassword = "" }

let private acquireDeploymentCredentials settings credentials =
    let response =
        callWebAppEndpoint settings credentials HttpMethod.Post "publishxml"
        |> AzurePublishXmlResponse.Parse
    let pubMethod =
        response.PublishProfiles
        |> Array.find (fun t -> t.PublishMethod = AzurePublishProfile)
    let idx = pubMethod.UserName.IndexOf '\\'
    { credentials with
        DeployUserName = pubMethod.UserName.Substring(idx + 1)
        DeployPassword = pubMethod.UserPwd }

let private areEqual a b =
    String.Equals(a, b, StringComparison.InvariantCultureIgnoreCase)

/// Reads WebApp configuration from the environment, allowing to change every parameter.
/// All parameters except `DeployPath` are required, but `DeployPath` must not be `null`.
///
/// ## Environment variables
///
///  - `TenantId` - `AZURE_TENANT_ID`
///  - `ClientId` - `AZURE_CLIENT_ID`
///  - `ClientSecret` - `AZURE_CLIENT_SECRET`
///  - `SubscriptionId` - `AZURE_SUBSCRIPTION_ID`
///  - `ResourceGroup` - `AZURE_RESOURCE_GROUP`
///  - `WebAppName` - `AZURE_WEBAPP`
///
/// ## Parameters
///
///  - `setParams` - Function used to override loaded parameters.
let readSiteSettingsFromEnv setParams =
    let validate s =
        if String.IsNullOrWhiteSpace s.TenantId       then failwith "You must specify tenant id"
        if String.IsNullOrWhiteSpace s.ClientId       then failwith "You must specify client id"
        if String.IsNullOrWhiteSpace s.ClientSecret   then failwith "You must specify client secret"
        if String.IsNullOrWhiteSpace s.SubscriptionId then failwith "You must specify subscription id"
        if String.IsNullOrWhiteSpace s.ResourceGroup  then failwith "You must specify resource group"
        if String.IsNullOrWhiteSpace s.WebAppName    then failwith "You must specify WebApp name"
        s
    { TenantId       = environVarOrDefault "AZURE_TENANT_ID"       ""
      ClientId       = environVarOrDefault "AZURE_CLIENT_ID"       ""
      ClientSecret   = environVarOrDefault "AZURE_CLIENT_SECRET"   ""
      SubscriptionId = environVarOrDefault "AZURE_SUBSCRIPTION_ID" ""
      ResourceGroup  = environVarOrDefault "AZURE_RESOURCE_GROUP"  ""
      WebAppName    = environVarOrDefault "AZURE_WEBAPP"         ""
      DeployPath     = "" }
    |> setParams |> validate

/// Acquires access token for the service principal and deployment credentials for the WebApp.
///
/// ## Parameters
///
///  - `settings` - WebApp settings with service principal credentials.
///
let acquireCredentials settings =
    use __ = traceStartTaskUsing "Azure.WebApps.AcquireCredentials" (sprintf "WebApp: %s" settings.WebAppName)

    let credentials = acquireAccessToken settings |> acquireDeploymentCredentials settings
    { Settings = settings; Credentials = credentials }

/// Starts the WebApp using ARM.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let startWebApp config =
    let settings = config.Settings
    let credentials = config.Credentials

    use __ = traceStartTaskUsing "Azure.WebApp.Start" (sprintf "WebApp: %s" settings.WebAppName)
    callWebAppEndpoint settings credentials HttpMethod.Post "start" |> ignore

/// Starts continuous WebJob in selected app using Kudu's API.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///  - `webjob` - WebJob name.
///
let startContinuousWebJob config webjob =
    let settings = config.Settings
    let credentials = config.Credentials

    use __ = traceStartTaskUsing "Azure.WebApps.StartWebJob" (sprintf "WebApp: %s; WebJob: %s" settings.WebAppName webjob)
    callWebAppSCMEndpoint settings credentials HttpMethod.Post (sprintf "continuouswebjobs/%s/start" webjob) |> ignore

/// Stops the WebApp using ARM.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let stopWebApp config =
    let settings = config.Settings
    let credentials = config.Credentials

    use __ = traceStartTaskUsing "Azure.WebApps.Stop" (sprintf "WebApp: %s" settings.WebAppName)
    callWebAppEndpoint settings credentials HttpMethod.Post "stop" |> ignore

/// Stops continuous WebJob in selected app using Kudu's API.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///  - `webjob` - WebJob name.
///
let stopContinuousWebJob config webjob =
    let settings = config.Settings
    let credentials = config.Credentials

    use __ = traceStartTaskUsing "Azure.WebApps.StopWebJob" (sprintf "WebApp: %s; WebJob: %s" settings.WebAppName webjob)
    callWebAppSCMEndpoint settings credentials HttpMethod.Post (sprintf "continuouswebjobs/%s/stop" webjob) |> ignore

/// Pushes the ZIP to Kudu's ZIP Controller and extracts it to specified path (`DeployPath`).
///
/// The WebApp should be stopped before performing this action, but this is not required. When performing on a live
/// machine, be prepared for receiving `500 Internal Server Error` from the endpoint saying that some file is locked.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///  - `file` - Path to the ZIP file that will be sent.
///
let pushZipFile config file =
    let settings = config.Settings
    let credentials = config.Credentials
    use __ = traceStartTaskUsing "Azure.WebApps.Upload" (sprintf "WebApp: %s, File: %s" settings.WebAppName file)
    let url = sprintf scmEndpoint settings.WebAppName ("zip/" + settings.DeployPath)
    traceVerbose <| sprintf "Reading ZIP %s" file
    let content = File.ReadAllBytes file
    traceVerbose <| sprintf "Uploading ZIP %s to the WebApp %s/%s" file settings.WebAppName settings.DeployPath
    Http.Request
        (url,
            httpMethod = HttpMethod.Put,
            headers = [makeBasicAuthHeader credentials],
            body = BinaryUpload content) |> ignore
    traceVerbose <| sprintf "ZIP %s uploaded successfully to the WebApp %s" file settings.WebAppName

/// Executes arbitrary command using Kudu's API.
///
/// ### Parameters
///
///  - `config` - WebApp configuration.
///  - `cmd` - The command to execute. Do not assume the shell as it may change. Run the shell explicitly.
///  - `dir` - The directory where the command should be executed.
///
let executeCommand config cmd dir =
    let settings = config.Settings
    let credentials = config.Credentials
    use __ = traceStartTaskUsing "Azure.WebApps.Command" (sprintf "WebApp: %s, Command: %s" settings.WebAppName cmd)
    let url = sprintf scmEndpoint settings.WebAppName "command"
    let content =
        JsonValue.Record
            [| "command", JsonValue.String cmd
               "dir", JsonValue.String dir |]
    let response =
        Http.RequestString
            (url,
                httpMethod = HttpMethod.Post,
                headers =
                    [ makeBasicAuthHeader credentials
                      HttpRequestHeaders.ContentType HttpContentTypes.Json ],
                body = TextRequest (content.ToString()))
    let parsed = response |> CommandResponse.Parse
    { Output = parsed.Output; Error = parsed.Error; ExitCode = parsed.ExitCode }

/// Checks if the site responds with `403 Site disabled` status.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let ensureWebAppRespondsWith403 config =
    let settings = config.Settings
    let statusCode, statusDescription = getSiteStatus settings
    statusCode = 403 && areEqual statusDescription "Site disabled"

/// Checks if the `dotnet` process is not running.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let ensureDotNetIsNotRunning config =
    let result = executeCommand config "powershell -NoProfile -Command \"Get-Process -Name 'dotnet'\"" ""
    result.ExitCode <> 0

/// Waits until the site is stopped (i.e. all the conditions are true).
///
/// This function repeatedly calls the root of the WebApp, waiting one second between requests.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let rec ensureWebAppIsStopped config conds =
    let settings = config.Settings
    let result = conds |> Seq.fold (fun o f -> o && f config) true
    if result then
        traceVerbose "Site has stopped"
    else
        traceVerbose "Site is still running, waiting..."
        System.Threading.Thread.Sleep 1000
        ensureWebAppIsStopped config conds

/// Waits until the .NET Core application running on the WebApp is stopped. Uses
/// `ensureWebAppRespondsWith403` and `ensureDotNetIsNotRunning`).
///
/// This function repeatedly calls the root of the WebApp, waiting one second between requests.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let ensureDotNetCoreAppIsStopped config =
    ensureWebAppIsStopped config
        [ ensureWebAppRespondsWith403
          ensureDotNetIsNotRunning ]

/// Stops the WebApp using ARM and waits until the site is really stopped (IIS is not running).
///
/// Uses `ensureWebAppIsStopped`.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let stopDotNetCoreAppAndWait config =
    let settings = config.Settings
    let credentials = config.Credentials
    use __ = traceStartTaskUsing "Azure.WebApps.Start" (sprintf "WebApp: %s" settings.WebAppName)
    callWebAppEndpoint settings credentials HttpMethod.Post "stop" |> ignore
    ensureDotNetCoreAppIsStopped config

