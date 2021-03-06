﻿module Fake.DeploymentAgent
    
open System
open System.IO
open System.Net
open System.Threading
open System.Diagnostics
open Fake.DeploymentHelper
open Fake.HttpListenerHelper

let mutable private logger : (string * EventLogEntryType) -> unit = ignore 

let private runDeployment args (ctx : HttpListenerContext) =
    let packageBytes = getBodyFromContext ctx

    let package,scriptFile = unpack false packageBytes
    let response = doDeployment package.Name scriptFile
    
    match response with
    | HttpClientHelper.Success -> logger (sprintf "Successfully deployed %s %s" package.Id package.Version, EventLogEntryType.Information)
    | response -> logger (sprintf "Deployment failed of %s %s failed" package.Id package.Version, EventLogEntryType.Information)

    response
    |> Json.serialize

let private getActiveReleases args (ctx : HttpListenerContext) =
    getActiveReleases workDir 
    |> HttpClientHelper.DeploymentResponse.QueryResult 
    |> Json.serialize

let private getAllReleases args (ctx : HttpListenerContext) = 
    getAllReleases workDir 
    |> HttpClientHelper.DeploymentResponse.QueryResult 
    |> Json.serialize

let private getAllReleasesFor (args:Map<_,_>) (ctx : HttpListenerContext) = 
    getAllReleasesFor workDir args.["app"]
    |> HttpClientHelper.DeploymentResponse.QueryResult 
    |> Json.serialize

let private getActiveReleaseFor (args:Map<_,_>) (ctx : HttpListenerContext) =
    getActiveReleaseFor workDir args.["app"]
    |> Seq.singleton
    |> HttpClientHelper.DeploymentResponse.QueryResult 
    |> Json.serialize

let private runRollbackToVersion (args:Map<_,_>) (ctx : HttpListenerContext) = 
    rollbackTo workDir args.["app"] args.["version"]
    |> Json.serialize

let routes =
    defaultRoutes
        @ [ "POST", "", runDeployment
            "GET", "/deployments/{app}/", getAllReleasesFor
            "GET", "/deployments/{app}?status=active", getActiveReleaseFor
            "PUT", "/deployments/{app}?version={version}", runRollbackToVersion
            "GET", "/deployments?status=active", getActiveReleases
            "GET", "/deployments/", getAllReleases ]

let start log serverName port = 
    logger <- log
    routes |> Seq.iter (fun (v,r,_) -> tracefn "%s %s" v r)
    HttpListenerHelper.start log serverName port (routes |> createRoutes)
