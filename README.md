# Fake.Azure.WebSites

Simple FAKE helper that makes deploying Azure WebSites a breeze.

## Description

This isn't a sophisticated wrapper of the Azure Resource Manager / Kudu API. Its main purpose is very simple - make publishing new version of apps to the Azure WebSites a reliable and fast process.

It boils down to stopping the App Service, sending ZIP file, extracting it and starting the service. Everything can be done with ARM and Kudu, but the overhead to make that happen (esp. authorization) can be quite big. The main purpose of this helper is to make that process a little easier by minimizing the operations needed and settings that needs to be specified.

## Usage

See `samples/dotnetcore/build.fsx` for a sample.
