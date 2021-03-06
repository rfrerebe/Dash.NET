﻿namespace Dash.NET

open Plotly.NET
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open System
open DynamicInvoke


type Dependency =
    {
        [<JsonProperty("id")>]
        Id: string
        [<JsonProperty("property")>]
        Property: string
    }
    static member create(id, property) = { Id = id; Property = property }
    static member toCompositeId (d:Dependency) = sprintf "%s.%s" d.Id d.Property
    static member ofList (dict:seq<string*string>) =
        dict
        |> Seq.map Dependency.create

type CallbackInput = Dependency
type CallbackOutput = Dependency
type CallbackState = Dependency

type ClientSideFunction =
    {
        [<JsonProperty("namespace")>]
        Namespace: string
        [<JsonProperty("function_name")>]
        FunctionName: string
    }

type RequestInput = 
    { 
        [<JsonProperty("id")>]
        Id: string
        [<JsonProperty("property")>]
        Property: string
        [<JsonProperty("value")>]
        Value: obj 
    }

///Type to deserialize calls to _dash-update-component
type CallbackRequest =
    {
        [<JsonProperty("output")>]
        Output: string
        [<JsonProperty("outputs")>]
        Outputs: CallbackOutput
        [<JsonProperty("changedPropIds")>]
        ChangedPropIds: string []
        [<JsonProperty("inputs")>]
        Inputs: RequestInput []
        [<JsonProperty("state")>]
        State:RequestInput []
    }

open Newtonsoft.Json
open Newtonsoft.Json.Linq
open System

type OutputConverter() =
    inherit JsonConverter<CallbackOutput> ()
    override _.ReadJson(reader, (objectType:Type), (existingValue:CallbackOutput), (hasExistingValue:bool), (serializer:JsonSerializer)) : CallbackOutput =
        let token = JToken.Load(reader)
        let res : CallbackOutput = token.ToObject<CallbackOutput>()
        res
    override _.WriteJson(writer:JsonWriter, value: CallbackOutput, serializer: JsonSerializer) =
            let value' = CallbackOutput.toCompositeId value
            let token = JToken.FromObject(value')
            token.WriteTo(writer)

//Central type for Callbacks. Creating an instance of this type and registering it on the callback map is the equivalent of the @app.callback decorator in python.
type Callback<'Function> 
    (
        Inputs: seq<CallbackInput>,
        Output: CallbackOutput,
        HandlerFunction: 'Function,
        ?State: seq<CallbackState>,
        ?PreventInitialCall: bool,
        ?ClientsideFunction: ClientSideFunction
    ) =
    [<JsonProperty("inputs")>]
    member _.Inputs = Inputs
    [<JsonProperty("output")>]
    [<JsonConverter(typeof<OutputConverter>)>]
    member _.Output = Output
    [<JsonIgnore()>]
    member _.HandlerFunction = HandlerFunction
    [<JsonProperty("state")>]
    member _.State = defaultArg State Seq.empty
    [<JsonProperty("prevent_initial_call")>]
    member _.PreventInitialCall = defaultArg PreventInitialCall true
    [<JsonProperty("clientside_function")>]
    member _.ClientsideFunction = ClientsideFunction
    [<JsonProperty("multi")>]
    member _.Multi = false // no multi callbacks supported atm

    //Necessary as generic types seem not te be unboxed as easily (problems arise e.g. when unboxing (box Callback<string,string>), as the og types used for
    //the generics are missing, therefore obj,obj is assumed and the cast fails)
    static member pack(handler: Callback<'Function>): Callback<obj> =
        Callback(
            handler.Inputs,
            handler.Output,
            (box handler.HandlerFunction),
            handler.State,
            handler.PreventInitialCall
        )

    //returns a boxed result of the dynamic invokation of the handler function
    static member eval (args: seq<obj>) (handler: Callback<'Function>) =
        invokeDynamic<obj> handler.HandlerFunction args

    //returns the result of the dynamic invokation of the handler function casted to the type of choice
    static member evalAs<'OutputType> (args: seq<obj>) (handler: Callback<'Function>) =
        invokeDynamic<'OutputType> handler.HandlerFunction args

    //returns the response object to send as response to a request to _dash-update-component that triggered this callback
    static member getResponseObject (args: seq<obj>) (handler: Callback<'Function>) =

        let evalResult =
            handler
            |> Callback.pack
            |> Callback.eval args

        match evalResult with
        | Ok r ->

            //This should be properly wrapped/typed
            let root = DynamicObj()
            let response = DynamicObj()
            let result = DynamicObj()

            result?(handler.Output.Property) <- r
            response?(handler.Output.Id) <- result

            root?multi <- true
            root?response <- response

            root

        | Error e -> failwith e.Message

type CallbackMap() =
    inherit DynamicObj()

    static member registerCallback
        (callback: Callback<'Function>)
        (callbackMap: CallbackMap)
        =
        let callbackId = callback.Output |> Dependency.toCompositeId
        callbackMap?(callbackId) <- (Callback.pack callback)
        callbackMap

    static member unregisterCallback (callbackId: string) (callbackMap: CallbackMap) =
        match (callbackMap.TryGetTypedValue<Callback<obj>> callbackId) with
        | Some _ ->
            callbackMap.Remove(callbackId) |> ignore
            callbackMap
        | None -> callbackMap

    static member getPackedCallbackById (callbackId: string) (callbackMap: CallbackMap)
        : Callback<obj>
        =
        match (callbackMap.TryGetTypedValue<Callback<obj>> callbackId) with
        | Some cHandler -> cHandler
        | None -> failwithf "No callback handler registered for id %s" callbackId

    static member toDependencies (callbackMap: CallbackMap) =
        let members = callbackMap.GetDynamicMemberNames()
        members
        |> Seq.map (fun cName ->
            CallbackMap.getPackedCallbackById cName callbackMap
        )

        
