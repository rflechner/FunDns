open System
open System.Net
open System.Threading
open Server

[<EntryPoint>]
let main argv =
  printfn "F# DNS server"
  
  use tokenSource = new CancellationTokenSource()

  Server.listen tokenSource.Token 5300
    (fun query ->
      async {
        do printfn "Questions: %A" query

        if query.Questions |> Array.exists (fun q -> q.Domain.EndsWith ".romcyber.com")
        then 
          return
            Found [|
                { Domain="www.romcyber.com"
                  Type=QueryType.A
                  Class=QueryClass.IN
                  TTL=TimeSpan.FromMinutes 20.
                  Ip=IPAddress.Parse "127.0.0.1"
                }
              |]
        else
          let ip = IPAddress.Parse "8.8.8.8"
          return ForwardTo (ip, 53)
      }
    ) |> Async.RunSynchronously

  0 // return an integer exit code
