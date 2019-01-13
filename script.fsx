#load "Server.fs"

open System
open System.Net
open System.Net.Sockets
open System.IO
open Server

// https://www.frameip.com/dns/
// http://www.tcpipguide.com/free/t_DNSMessageHeaderandQuestionSectionFormat.htm
// https://www.ietf.org/rfc/rfc1035.txt

let query = 
  {Header = {Id = 56414us;
             Flags = {Qr = 0uy;
                      OpCode = 0uy;
                      Aa = 0uy;
                      Tc = 0uy;
                      Rd = 1uy;
                      Ra = 0uy;
                      Z = 0uy;
                      Rcode = 0uy;};
             QdCount = 1us;
             AnCount = 0us;
             NsCount = 0us;
             ArCount = 0us;};
   Questions = [|{Domain = "www.romcyber.com";
                  Type = QueryType.A;
                  Class = QueryClass.IN }|];
   Answers = [||];}

let data = Writer.responseDatagram query

let googleIp = new IPEndPoint(IPAddress.Parse "8.8.8.8", 53)
let localIp = new IPEndPoint(IPAddress.Parse "127.0.0.1", 5300)
let client = new UdpClient()
client.Connect localIp

async {
  do! client.SendAsync(data, data.Length) |> Async.AwaitTask |> Async.Ignore
  let! v = client.ReceiveAsync() |> Async.AwaitTask
  use stream = new MemoryStream (v.Buffer)
  use reader = new BinaryReader(stream)
  let rs = Reader.readQuery reader
  printfn "rs: %A" rs
} |> Async.RunSynchronously


client.Dispose()
