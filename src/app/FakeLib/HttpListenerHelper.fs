﻿module Fake.HttpListenerHelper

open System
open System.IO
open System.Net
open System.Net.NetworkInformation
open System.Threading
open System.Diagnostics
open System.Text.RegularExpressions

type Route = {
        Verb : string
        Path : string
        Handler : Map<string,string> -> HttpListenerContext -> string        
    }
    with 
        override x.ToString() = sprintf "%s %s" x.Verb x.Path              

type RouteResult =
    { Route:Route
      Parameters: Map<string,string> }
        
let private listener serverName port = 
    let listener = new HttpListener()
    listener.Prefixes.Add(sprintf "http://%s:%s/fake/" serverName port)
    listener.Start()
    listener

let private writeResponse (ctx : HttpListenerContext) (str : string) = 
    let response = Text.Encoding.UTF8.GetBytes(str)
    ctx.Response.ContentLength64 <- response.Length |> int64
    ctx.Response.ContentEncoding <- Text.Encoding.UTF8
    ctx.Response.Close(response, true)

let placeholderRegex = new Regex("{([^}]+)}",RegexOptions.Compiled)

let routeMatcher route =
    let dict = new System.Collections.Generic.Dictionary<int,string>()

    let normalized = route.Path.Replace("?",@"\?").Trim '/'

    let pattern = 
        let pat = ref normalized
        [for m in route.Path |> placeholderRegex.Matches -> m.Groups.[1].Value]
        |> Seq.iteri (fun i p ->
                dict.Add(i,p)
                pat := (!pat).Replace("{" + p + "}","([^/?]+)"))
        !pat
    
    let r = new Regex( "^" + pattern + "$", RegexOptions.Compiled)

    fun verb (url:string) ->
        let searchedURL = url.Trim('/').ToLower()

        if route.Verb <> verb then None else      
        if route.Path = searchedURL then Some { Route = route; Parameters = Map.empty } else
        
        match r.Match searchedURL with
        | m when m.Success -> 
            let parameters =
                [for g in m.Groups -> g.Value]
                |> List.tail
                |> Seq.mapi (fun i p -> dict.[i],p)
                |> Map.ofSeq

            Some { Route = route; Parameters = parameters }
        | _ -> None


let private routeRequest log (ctx : HttpListenerContext) routeMatchers =     
    try
        let verb = ctx.Request.HttpMethod
        let url = ctx.Request.RawUrl.Replace("fake/", "")

        match routeMatchers |> Seq.tryPick (fun r -> r verb url) with
        | Some routeResult ->
            routeResult.Route.Handler routeResult.Parameters ctx 
              |> writeResponse ctx
        | None -> writeResponse ctx (sprintf "Unknown route %s" ctx.Request.Url.AbsoluteUri)
    with e ->
        let msg = sprintf "Fake Deploy Request Error:\n\n%A" e
        log (msg, EventLogEntryType.Error)
        writeResponse ctx msg

let private getStatus args (ctx : HttpListenerContext) = "Http listener is running"

let defaultRoutes =
    [ "GET", "", getStatus] 

let createRoutes routes = 
    routes
    |> Seq.map (fun (verb, route : string, func) ->        
        { Verb = verb
          Path = route.Trim([|'/'; '\\'|]).ToLower()
          Handler = func })
    |> Seq.map routeMatcher

let CreateDefaultRequestMap() = createRoutes defaultRoutes

let matchRoute routes verb url =
    routes |> Seq.map routeMatcher |> Seq.tryPick (fun r -> r verb url)

let getBodyFromContext (ctx : HttpListenerContext) = 
    let readAllBytes (s : Stream) =
        let ms = new MemoryStream()
        let buf = Array.zeroCreate 8192
        let rec impl () = 
            let read = s.Read(buf, 0, buf.Length) 
            if read > 0 then 
                ms.Write(buf, 0, read)
                impl ()
        impl ()
        ms
    if ctx.Request.HasEntityBody 
    then (readAllBytes ctx.Request.InputStream).ToArray() 
    else failwith "Attempted to read body from request when there is not one"

let getFirstFreePort() =
    let defaultPort = 8080
    let usedports = NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners() |> Seq.map (fun x -> x.Port)
    let ports = seq { for port in defaultPort .. defaultPort + 2048 do yield port }
    let port = ports |> Seq.find (fun p -> not <| Seq.contains p usedports)
    port.ToString()

let getPort configPort =    
    match configPort with
    | "*" -> getFirstFreePort()
    | _ -> configPort 


type Listener =
  { ServerName: string
    Port: string
    CancelF: unit -> unit }
  with
      member x.Cancel() = x.CancelF()
      member x.RootUrl = sprintf "http://%s:%s/fake/" x.ServerName x.Port

let emptyListener = { 
    ServerName = ""
    Port = ""
    CancelF = id }

let start log serverName port requestMap =
    let cts = new CancellationTokenSource()
    let usedPort = getPort port
    let listenerLoop = 
        async {
            try 
                log (sprintf "Trying to start Fake Deploy server @ %s port %s" serverName usedPort, EventLogEntryType.Information)
                use l = listener serverName usedPort
                let prefixes = l.Prefixes |> separated ","
                log (sprintf "Fake Deploy now listening @ %s" prefixes, EventLogEntryType.Information)
                while true do
                    routeRequest log (l.GetContext()) requestMap
            with e ->
                log (sprintf "Listener Error:\n\n%A" e, EventLogEntryType.Error)             
        }

    Async.Start(listenerLoop, cts.Token)
    { ServerName = serverName; Port = usedPort; CancelF = cts.Cancel }

let startWithConsoleLogger serverName port requestMap =
    start TraceHelper.logToConsole serverName port requestMap