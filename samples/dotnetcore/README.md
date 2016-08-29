# .NET Core Web App on Azure App Services

This is a stripped-down version of the default .NET Core web project created using `dotnet new -t web`. It has a FAKE build script that is able to clean, build, publish and upload the app directly to an Azure WebApps.

See `build.fsx` for the usage of `Fake.Azure.WebApps`.
