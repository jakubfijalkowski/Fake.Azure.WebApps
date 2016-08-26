module Fake.Azure.WebSites

open System
open System.Text
open System.IO
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

type AzureWebSiteSettings = {
    TenantId       : string
    ClientId       : string
    ClientSecret   : string
    SubscriptionId : string
    ResourceGroup  : string
    WebSiteName    : string
    DeployPath     : string
}

type AzureWebSiteCredentials = {
    AccessToken    : string
    DeployUserName : string
    DeployPassword : string
}

let private makeBearerHeader = (+) "Bearer " >> HttpRequestHeaders.Authorization
let private makeBasicAuthHeader cred =
    sprintf "%s:%s" cred.DeployUserName cred.DeployPassword
    |> Encoding.UTF8.GetBytes
    |> Convert.ToBase64String
    |> (+) "Basic "
    |> HttpRequestHeaders.Authorization

let private getSiteStatus settings =
    let url = sprintf siteAddress settings.WebSiteName
    Http.Request(url, silentHttpErrors = true) |> fun r -> r.StatusCode

let private makeEmptyRequest url httpMethod credentials =
    let headers = [ makeBearerHeader credentials.AccessToken ]
    if httpMethod = HttpMethod.Get
    then Http.RequestString (url, httpMethod = httpMethod, headers = headers)
    else Http.RequestString (url, httpMethod = httpMethod, headers = headers, body = BinaryUpload [||])

let private callWebsiteEndpoint settings credentials httpMethod action =
    traceVerbose <| "Calling WebSite action " + action
    let url = sprintf siteEndpoint settings.SubscriptionId settings.ResourceGroup settings.WebSiteName action
    let response = makeEmptyRequest url httpMethod credentials
    traceVerbose <| sprintf "Action %s on WebSite %s finished successfully" action settings.WebSiteName
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
        callWebsiteEndpoint settings credentials HttpMethod.Post "publishxml"
        |> AzurePublishXmlResponse.Parse
    let pubMethod =
        response.PublishProfiles
        |> Array.find (fun t -> t.PublishMethod = AzurePublishProfile)
    let idx = pubMethod.UserName.IndexOf '\\'
    { credentials with
        DeployUserName = pubMethod.UserName.Substring(idx + 1)
        DeployPassword = pubMethod.UserPwd }

/// Reads WebSite configuration from the environment, allowing to change every parameter.
/// All parameters except `DeployPath` are required, but `DeployPath` must not be `null`.
///
/// ## Environment variables
///
///  - `TenantId` - `AZURE_TENANT_ID`
///  - `ClientId` - `AZURE_CLIENT_ID`
///  - `ClientSecret` - `AZURE_CLIENT_SECRET`
///  - `SubscriptionId` - `AZURE_SUBSCRIPTION_ID`
///  - `ResourceGroup` - `AZURE_RESOURCE_GROUP`
///  - `WebSiteName` - `AZURE_WEBSITE`
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
        if String.IsNullOrWhiteSpace s.WebSiteName    then failwith "You must specify WebSite name"
        s
    { TenantId       = environVarOrDefault "AZURE_TENANT_ID"       ""
      ClientId       = environVarOrDefault "AZURE_CLIENT_ID"       ""
      ClientSecret   = environVarOrDefault "AZURE_CLIENT_SECRET"   ""
      SubscriptionId = environVarOrDefault "AZURE_SUBSCRIPTION_ID" ""
      ResourceGroup  = environVarOrDefault "AZURE_RESOURCE_GROUP"  ""
      WebSiteName    = environVarOrDefault "AZURE_WEBSITE"         ""
      DeployPath     = "" }
    |> setParams |> validate

/// Acquires access token for the service principal and deployment credentials for the WebSite.
///
/// ## Parameters
///
///  - `settings` - WebSite settings with service principal credentials.
///
let acquireCredentials settings =
    traceStartTask "Azure.WebSites.AcquireCredentials" (sprintf "WebSite: %s" settings.WebSiteName)
    try
        acquireAccessToken settings |> acquireDeploymentCredentials settings
    finally
        traceEndTask "Azure.WebSites.AcquireCredentials" ""

/// Starts the WebSite using ARM.
///
/// ## Parameters
///
///  - `settings` - WebSite settings.
///  - `credentials` - Azure and WebSite credentials acquired with `acquireCredentials`.
///
let startWebSite settings credentials =
    traceStartTask "Azure.Website.Start" (sprintf "WebSite: %s" settings.WebSiteName)
    try
        callWebsiteEndpoint settings credentials HttpMethod.Post "start" |> ignore
    finally
        traceEndTask "Azure.WebSites.Start" ""

/// Stops the WebSite using ARM.
///
/// ## Parameters
///
///  - `settings` - WebSite settings.
///  - `credentials` - Azure and WebSite credentials acquired with `acquireCredentials`.
///
let stopWebSite settings credentials =
    traceStartTask "Azure.WebSites.Start" (sprintf "WebSite: %s" settings.WebSiteName)
    try
        callWebsiteEndpoint settings credentials HttpMethod.Post "stop" |> ignore
    finally
        traceEndTask "Azure.WebSites.Start" ""

/// Waits until the site is stopped (i.e. returns `503 Unavailable` status code).
///
/// This function repeatedly calls the root of the website, waiting one second between requests.
///
/// ## Parameters
///
///  - `settings` - WebSite settings.
///
let rec ensureWebsiteIsStopped settings =
    let response = getSiteStatus settings
    if response <> 503 then
        traceVerbose "Site is still running, waiting..."
        System.Threading.Thread.Sleep 1000
        ensureWebsiteIsStopped settings
    else
        traceVerbose "Site has stopped"

/// Stops the WebSite using ARM and waits until the site is really stopped (IIS is not running).
///
/// Uses `ensureWebsiteIsStopped`.
///
/// ## Parameters
///
///  - `settings` - WebSite settings.
///  - `credentials` - Azure and WebSite credentials acquired with `acquireCredentials`.
///
let stopWebSiteAndWait settings credentials =
    traceStartTask "Azure.WebSites.Start" (sprintf "WebSite: %s" settings.WebSiteName)
    try
        callWebsiteEndpoint settings credentials HttpMethod.Post "stop" |> ignore
        ensureWebsiteIsStopped settings
    finally
        traceEndTask "Azure.WebSites.Start" ""

/// Pushes the ZIP to Kudu's ZIP Controller and extracts it to specified path (`DeployPath`).
///
/// The WebSite should be stopped before performing this action, but this is not required. When performing on a live
/// machine, be prepared for receiving `500 Internal Server Error` from the endpoint saying that some file is locked.
///
/// ## Parameters
///
///  - `settings` - WebSite settings.
///  - `credentials` - Azure and WebSite credentials acquired with `acquireCredentials`.
///  - `file` - Path to the ZIP file that will be sent.
let pushZipFile settings credentials file =
    traceStartTask "Azure.WebSites.Upload" (sprintf "WebSite: %s, File: %s" settings.WebSiteName file)
    try
        let url = sprintf zipEndpoint settings.WebSiteName settings.DeployPath
        traceVerbose <| sprintf "Reading ZIP %s" file
        let content = File.ReadAllBytes file
        traceVerbose <| sprintf "Uploading ZIP %s to the WebSite %s/%s" file settings.WebSiteName settings.DeployPath
        Http.Request
            (url,
             httpMethod = HttpMethod.Put,
             headers = [makeBasicAuthHeader credentials],
             body = BinaryUpload content) |> ignore
        traceVerbose <| sprintf "ZIP %s uploaded successfully to the website %s" file settings.WebSiteName
    finally
        traceEndTask "Azure.WebSites.Upload" ""
