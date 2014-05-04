﻿namespace Fredis

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fredis

type ExceptionInfo<'T> = 
    | ExceptionInfo of string * 'T * Exception

type Actor<'Tin, 'Tout> internal (redis : Redis, id : string, ?computation : 'Tin -> Async<'Tout>) = 
    let children = Dictionary<string, Actor<'Tout, _>>()
    let mutable started = false
    let mutable cts = Unchecked.defaultof<CancellationTokenSource>
    let awaitMessageHandle = new AutoResetEvent(false)
    let awaitResultHandle = new AutoResetEvent(false)
    let prefix = id + ":Mailbox:"
    let inboxKey = prefix + ":inbox"
    let pipelineKey = prefix + ":pipeline"
    let resultsKey = prefix + ":results"
    let channelKey = prefix + ":channel"
    let errorsKey = prefix + ":errors"
    let mutable errorHandler = Unchecked.defaultof<Actor<ExceptionInfo<'Tin>, _>>
    // global limit for number of tasks
    static let semaphor = new SemaphoreSlim(256)
    
    do 
        redis.Subscribe(channelKey, 
                        Action<string, string>(fun channel message -> 
                            match message with
                            | "" -> awaitMessageHandle.Set() |> ignore
                            | x -> awaitResultHandle.Set() |> ignore))
    
    let rec await timeout = 
        async { 
            //let! message = !!redis.RPopAsync("")
            // atomically move to safe place while processing
            let lua = @"
local result = redis.call('RPOP', KEYS[1])
if result ~= nil
    redis.call('HSET', KEYS[2], ARGV[1], result)
end
return result"
            // TODO add ZSet with timestamp as rank to monitor the pipeline state
            let pipelineId = Guid.NewGuid().ToString("N")
            let! message = !!redis.EvalAsync<'T * string>(lua, [| inboxKey; pipelineKey |], [| pipelineId |])
            if Object.Equals(message, null) then 
                let! recd = Async.AwaitWaitHandle(awaitMessageHandle, timeout)
                if recd then return! await timeout
                else return raise (TimeoutException("Receive timed out"))
            else return message, pipelineId
        }
    
    member this.Id = id
    member this.Children = children.Keys
    member this.QueueLength = int (redis.LLen(inboxKey))
    
    member this.ErrorHandler 
        with get () = errorHandler
        and set (eh) = errorHandler <- eh
    
    member private this.Computation = computation
    
    member this.Start() : unit = 
        if computation.IsNone then failwith "Cannot start an actor without computation"
        cts <- new CancellationTokenSource()
        async { 
            while not cts.Token.IsCancellationRequested do
                do! !~semaphor.WaitAsync(cts.Token)
                let! msg = await Timeout.Infinite
                let payload = (fst (fst msg))
                let messageId = (snd (fst msg))
                let pipelineId = snd msg
                async { 
                    try 
                        let! result = computation.Value payload
                        children.Values |> Seq.iter (fun a -> a.Post(result))
                        redis.HDelAsync(pipelineKey, pipelineId) |> ignore
                        if messageId <> "" then 
                            redis.HSet(resultsKey, messageId, result, When.Always, true) |> ignore
                            redis.PublishAsync<string>(channelKey, messageId) |> ignore
                    with e -> 
                        let ei = ExceptionInfo(id, payload, e)
                        redis.LPush<ExceptionInfo<'Tin>>(errorsKey, ei, When.Always, true) |> ignore
                        if errorHandler <> Unchecked.defaultof<Actor<ExceptionInfo<'Tin>, _>> then errorHandler.Post(ei)
                }
                |> Async.Start
                semaphor.Release() |> ignore
        }
        |> Async.Start
        started <- true
    
    member this.Stop() = 
        if started then 
            started <- false
            cts.Cancel |> ignore
    
    member this.Post(message : 'Tin, ?highPriority : bool) : unit = 
        let highPriority = defaultArg highPriority false
        if highPriority then redis.LPushAsync<'Tin * string>(inboxKey, (message, "")) |> ignore
        else redis.RPushAsync<'Tin * string>(inboxKey, (message, "")) |> ignore
        awaitMessageHandle.Set() |> ignore
        redis.PublishAsync<string>(channelKey, "") |> ignore
    
    member this.PostAndReply(message : 'Tin, ?highPriority : bool, ?millisecondsTimeout) : Async<'Tout> = 
        let highPriority = defaultArg highPriority false
        let millisecondsTimeout = defaultArg millisecondsTimeout Timeout.Infinite
        match started with
        | true -> 
            let pipelineId = Guid.NewGuid().ToString("N")
            redis.HSet<'Tin>(pipelineKey, pipelineId, message, When.Always, true) |> ignore // save message
            async { 
                let! result = computation.Value message
                children.Values |> Seq.iter (fun a -> a.Post(result))
                redis.HDelAsync(pipelineKey, pipelineId) |> ignore
                return result
            }
        | false -> 
            let resultId = Guid.NewGuid().ToString("N")
            if highPriority then redis.LPushAsync<'Tin * string>(inboxKey, (message, resultId)) |> ignore
            else redis.RPushAsync<'Tin * string>(inboxKey, (message, resultId)) |> ignore
            awaitMessageHandle.Set() |> ignore
            redis.PublishAsync<string>(channelKey, "") |> ignore // no resultId here because we notify recievers that in turn will notify callers about results
            let rec awaitResult timeout = 
                async { 
                    let! message = !!redis.HGetAsync<'Tout>(resultsKey, resultId)
                    if Object.Equals(message, null) then 
                        let! recd = Async.AwaitWaitHandle(awaitResultHandle, timeout)
                        if recd then return! awaitResult timeout
                        else return raise (TimeoutException("PostAndReply timed out"))
                    else return message
                }
            awaitResult millisecondsTimeout
    
    // C# naming style and return type
    member this.PostAndReplyAsync(message : 'Tin, ?highPriority : bool, ?millisecondsTimeout) : Task<'Tout> = 
         let res : Async<'Tout> = this.PostAndReply(message, highPriority??=false, millisecondsTimeout??=Timeout.Infinite) 
         res |> Async.StartAsTask

    member this.Link(actor : Actor<'Tout, _>) = 
        children.Add(actor.Id, actor)
        this

    member this.UnLink(actor : Actor<'Tout, _>) : bool = children.Remove(actor.Id)

    interface IDisposable with
        member x.Dispose() = 
            cts.Cancel |> ignore
            awaitMessageHandle.Dispose()
            awaitResultHandle.Dispose()
            cts.Dispose()
            semaphor.Dispose()