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
let private zipEndpoint : Printf.StringFormat<_> = "https://%s.scm.azurewebsites.net/api/zip/%s"
let private siteAddress : Printf.StringFormat<_> = "https://%s.azurewebsites.net"

type private AzureTokenResponse = JsonProvider<"""{"token_type":"","expires_in":"","ext_expires_in":"","expires_on":"","not_before":"","resource":"","access_token":""}""">
type private AzurePublishXmlResponse = XmlProvider<"""<publishData><publishProfile publishMethod="" userName="" userPWD="" /><publishProfile publishMethod="" userName="" userPWD="" /></publishData>""">

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
    traceStartTask "Azure.WebApps.AcquireCredentials" (sprintf "WebApp: %s" settings.WebAppName)
    try
        let credentials = acquireAccessToken settings |> acquireDeploymentCredentials settings
        { Settings = settings; Credentials = credentials }
    finally
        traceEndTask "Azure.WebApps.AcquireCredentials" ""

/// Starts the WebApp using ARM.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let startWebApp config =
    let settings = config.Settings
    let credentials = config.Credentials
    traceStartTask "Azure.WebApp.Start" (sprintf "WebApp: %s" settings.WebAppName)
    try
        callWebAppEndpoint settings credentials HttpMethod.Post "start" |> ignore
    finally
        traceEndTask "Azure.WebApps.Start" ""

/// Stops the WebApp using ARM.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let stopWebApp config =
    let settings = config.Settings
    let credentials = config.Credentials
    traceStartTask "Azure.WebApps.Start" (sprintf "WebApp: %s" settings.WebAppName)
    try
        callWebAppEndpoint settings credentials HttpMethod.Post "stop" |> ignore
    finally
        traceEndTask "Azure.WebApps.Start" ""

/// Waits until the site is stopped (i.e. returns `503 Unavailable` status code).
///
/// This function repeatedly calls the root of the WebApp, waiting one second between requests.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let rec ensureWebAppIsStopped config =
    let settings = config.Settings
    let statusCode, statusDescription = getSiteStatus settings
    if statusCode = 403 && areEqual statusDescription "Site disabled" then
        traceVerbose "Site has stopped"
    else
        traceVerbose "Site is still running, waiting..."
        System.Threading.Thread.Sleep 1000
        ensureWebAppIsStopped config

/// Stops the WebApp using ARM and waits until the site is really stopped (IIS is not running).
///
/// Uses `ensureWebAppIsStopped`.
///
/// ## Parameters
///
///  - `config` - WebApp configuration.
///
let stopWebAppAndWait config =
    let settings = config.Settings
    let credentials = config.Credentials
    traceStartTask "Azure.WebApps.Start" (sprintf "WebApp: %s" settings.WebAppName)
    try
        callWebAppEndpoint settings credentials HttpMethod.Post "stop" |> ignore
        ensureWebAppIsStopped config
    finally
        traceEndTask "Azure.WebApps.Start" ""

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
    traceStartTask "Azure.WebApps.Upload" (sprintf "WebApp: %s, File: %s" settings.WebAppName file)
    try
        let url = sprintf zipEndpoint settings.WebAppName settings.DeployPath
        traceVerbose <| sprintf "Reading ZIP %s" file
        let content = File.ReadAllBytes file
        traceVerbose <| sprintf "Uploading ZIP %s to the WebApp %s/%s" file settings.WebAppName settings.DeployPath
        Http.Request
            (url,
             httpMethod = HttpMethod.Put,
             headers = [makeBasicAuthHeader credentials],
             body = BinaryUpload content) |> ignore
        traceVerbose <| sprintf "ZIP %s uploaded successfully to the WebApp %s" file settings.WebAppName
    finally
        traceEndTask "Azure.WebApps.Upload" ""
