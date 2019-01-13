module Server

open System
open System.IO
open System.Text
open System.Net
open System.Net.Sockets
open System.Threading

type BinaryReader with
  member __.ReadBigEndianUInt16 () =
    let b1 = __.ReadByte()
    let b2 = __.ReadByte()
    uint16 (b1 ||| b2 <<< 8)

let readBits offset length (buffer:byte) =
  (buffer >>> offset) &&& ~~~(0xffuy <<< length)

let writeBits offset length buffer (value:byte) =
  let mask = ~~~(0xffuy <<< length)
  let v = (buffer &&& mask) <<< offset
  v ||| (value &&& ~~~(mask <<< offset))

type BinaryWriter with
  member __.WriteBigEndianUInt16(value:uint16) =
    __.BaseStream.Write([| byte (value >>> 8) ; byte value |], 0, 2) 
  member __.WriteBigEndianInt32(value:int32) =
    let data =
      [|
        byte value 
        byte (value >>> 8)
        byte (value >>> 16)
        byte (value >>> 24)
      |]
    __.BaseStream.Write(data, 0, 4)

type Header =
  { Id:uint16
    Flags:HeaderFlags
    QdCount:uint16
    AnCount:uint16
    NsCount:uint16
    ArCount:uint16 }
 and HeaderFlags =
  { Qr:byte; OpCode:byte
    Aa:byte; Tc:byte
    Rd:byte; Ra:byte
    Z:byte; Rcode:byte }

[<RequireQualifiedAccess>]
module HeaderFlags =
  let toBytes (h:HeaderFlags) =
    let flag1 =
      0uy 
      |> writeBits 7 1 h.Qr
      |> writeBits 3 1 h.OpCode
      |> writeBits 2 1 h.Aa
      |> writeBits 1 1 h.Tc
      |> writeBits 0 1 h.Rd
    let flag2 =
      0uy 
      |> writeBits 7 1 h.Ra
      |> writeBits 6 3 h.Z
      |> writeBits 0 4 h.Rcode
    [|flag1;flag2|]

type QueryType =
  | A     = 1  //a host address
  | NS    = 2  //an authoritative name server
  | MD    = 3  //a mail destination (Obsolete - use MX)
  | MF    = 4  //a mail forwarder (Obsolete - use MX)
  | CNAME = 5  //the canonical name for an alias
  | SOA   = 6  //marks the start of a zone of authority
  | MB    = 7  //a mailbox domain name (EXPERIMENTAL)
  | MG    = 8  //a mail group member (EXPERIMENTAL)
  | MR    = 9  //a mail rename domain name (EXPERIMENTAL)
  | NULL  = 10 // a null RR (EXPERIMENTAL)
  | WKS   = 11 // a well known service description
  | PTR   = 12 // a domain name pointer
  | HINFO = 13 // host information
  | MINFO = 14 // mailbox or mail list information
  | MX    = 15 // mail exchange
  | TXT   = 16 // text strings

type QueryClass = 
  | IN = 1
  | ANY = 255

type Query =
  { Header:Header
    Questions:Question array
    Answers:Answer array }
and Question = 
  { Domain:string
    Type:QueryType
    Class:QueryClass }
and Answer = 
  { Domain:string
    Type:QueryType
    Class:QueryClass
    TTL:TimeSpan
    Ip:IPAddress }

module Reader =
  let readHeader (reader:BinaryReader) =
    let id = reader.ReadUInt16()
    let [|flag1;flag2|] = reader.ReadBytes 2
    let qr = flag1 |> readBits 7 1
    let opCode = flag1 |> readBits 3 4
    let aa = flag1 |> readBits 2 1
    let tc = flag1 |> readBits 1 1
    let rd = flag1 |> readBits 0 1
    let ra = flag2 |> readBits 7 1
    let z = flag2 |> readBits 6 3
    let rcode = flag2 |> readBits 0 4
    let qdCount = reader.ReadBigEndianUInt16()
    let anCount = reader.ReadBigEndianUInt16()
    let nsCount = reader.ReadBigEndianUInt16()
    let arCount = reader.ReadBigEndianUInt16()
    { Id=id
      Flags=
        { Qr=qr; OpCode=opCode
          Aa=aa; Tc=tc
          Rd=rd; Ra=ra
          Z=z; Rcode=rcode }
      QdCount=qdCount
      AnCount=anCount
      NsCount=nsCount
      ArCount=arCount }

  let readDomain (reader:BinaryReader) =
    let readLabel (size:byte) =
      let label = size |> int |> reader.ReadBytes
      let nextSize = reader.ReadByte()
      label, nextSize
    let rec loop size (acc:byte array list) =
      match size with
      | 0uy -> 
          let labels = acc |> Seq.rev |> Seq.map Encoding.ASCII.GetString |> Seq.toArray
          System.String.Join(".", labels)
      | _ -> 
          let (label, size') = readLabel size
          loop size' (label :: acc)
    let size = reader.ReadByte()
    loop size []

  let readQueryType (reader:BinaryReader) : QueryType =
    reader.ReadBigEndianUInt16() |> int |> LanguagePrimitives.EnumOfValue

  let readQueryClass (reader:BinaryReader) : QueryClass =
    reader.ReadBigEndianUInt16() |> int |> LanguagePrimitives.EnumOfValue

  let readQuestion (reader:BinaryReader) =
    let domain = reader |> readDomain
    let queryType = reader |> readQueryType
    let queryClass = reader |> readQueryClass
    { Domain=domain
      Type=queryType
      Class=queryClass }

  let readQuery reader =
    let header = reader |> readHeader
    let questions = 
      { 0 .. int header.QdCount-1 }
      |> Seq.map (fun _ -> reader |> readQuestion)
      |> Seq.toArray
    { Header=header
      Questions=questions
      Answers=Array.empty }

module Writer =
  let writeResponse q (output:Stream) =
    use writer = new BinaryWriter(output)

    writer.Write q.Header.Id
    q.Header.Flags |> HeaderFlags.toBytes |> writer.Write
    writer.WriteBigEndianUInt16 q.Header.QdCount

    writer.WriteBigEndianUInt16 q.Header.AnCount
    writer.WriteBigEndianUInt16 q.Header.NsCount
    writer.WriteBigEndianUInt16 q.Header.ArCount

    let writeDomain (domain:string) =
      let labels = domain.Split([|'.'|], StringSplitOptions.RemoveEmptyEntries)
      labels
      |> Seq.iter (
          fun label -> 
            writer.Write (byte label.Length)
            label |> Encoding.ASCII.GetBytes |> writer.Write
          )
      writer.Write 0uy

    for question in q.Questions do
      writeDomain question.Domain
      let b:int = LanguagePrimitives.EnumToValue question.Type
      writer.WriteBigEndianUInt16 (uint16 b)
      let b':int = LanguagePrimitives.EnumToValue question.Class
      writer.WriteBigEndianUInt16 (uint16 b')

    for answer in q.Answers do
      writeDomain answer.Domain
      let b:int = LanguagePrimitives.EnumToValue answer.Type
      writer.WriteBigEndianUInt16 (uint16 b)
      let b':int = LanguagePrimitives.EnumToValue answer.Class
      writer.WriteBigEndianUInt16 (uint16 b')

      writer.WriteBigEndianInt32 (int32 answer.TTL.TotalSeconds)

      let address = answer.Ip.GetAddressBytes()
      writer.WriteBigEndianUInt16 (uint16 address.Length)
      writer.Write address

  let responseDatagram q = 
    use output = new MemoryStream()
    output |> writeResponse q
    output.ToArray()

type Reply =
  | Found of Answer array
  | ForwardTo of IPAddress * port:int

let listen (cancelToken:CancellationToken) port handle =
  let ip = new IPEndPoint(IPAddress.Any, port)
  async {
    use server = new UdpClient(ip)
    while not cancelToken.IsCancellationRequested do
      let! v = server.ReceiveAsync() |> Async.AwaitTask
      use stream = new MemoryStream(v.Buffer)
      use reader = new BinaryReader(stream)
      let query = reader |> Reader.readQuery
      let! result = handle query
      match result with
      | Found answers ->
        let rs = 
          {
              query with
                Answers = answers
                Header = 
                  {
                    query.Header with
                      AnCount = uint16 answers.Length
                      Flags =
                      {
                        query.Header.Flags with
                          Qr = 1uy
                      }
                  }
            }
        let data = Writer.responseDatagram rs
        let ip' = v.RemoteEndPoint
        do! server.SendAsync(data, data.Length, ip') |> Async.AwaitTask |> Async.Ignore
      | ForwardTo (ip, port) -> 
          let endpoint = new IPEndPoint(ip, port)
          use client = new UdpClient()
          client.Connect endpoint
          do! client.SendAsync(v.Buffer, v.Buffer.Length) |> Async.AwaitTask |> Async.Ignore
          let! v' = client.ReceiveAsync() |> Async.AwaitTask
          do! server.SendAsync(v'.Buffer, v'.Buffer.Length, v.RemoteEndPoint) |> Async.AwaitTask |> Async.Ignore
  }
