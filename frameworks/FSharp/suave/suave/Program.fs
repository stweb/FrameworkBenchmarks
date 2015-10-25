// Techempower test cases 
// implemented by Stefan Weber

open System
open System.Text
open System.Configuration
open System.Runtime.Serialization
open System.Web

open System.Data
open MySql.Data.MySqlClient

open Suave     
open Suave.Http
open Suave.Http.Applicatives
open Suave.Http.Successful 
open Suave.Http.RequestErrors
open Suave.Types
open Suave.Json

let MYSQL_CONNECTION_STRING = 
    ConfigurationManager.AppSettings.["ConnectionString.MySQL"]

// Suave.Json uses System.Runtime.Serialization.Json which requires some annotations
[<DataContract>]
type Message = { 
    [<field: DataMember(Name = "message")>]
    message: string  
}   

[<DataContract>]
type World = { 
    [<field: DataMember(Name = "id")>]
    id: int; 
    [<field: DataMember(Name = "randomNumber")>]
    randomNumber: int 
}

[<DataContract>]
type Fortune = { 
    [<field: DataMember(Name = "id")>]
    Id: int; 
    [<field: DataMember(Name = "message")>]
    Message: string
}

// map object to JSON response
let json o =
    toJson o |> Successful.ok 
    >>= Writers.setMimeType "application/json"

let random = new Random()

#if NEVER
open System.Data.Common

let getConn (providerName:string) =
    let connectionSettings = ConfigurationManager.ConnectionStrings.[providerName]
    let factory = DbProviderFactories.GetFactory(connectionSettings.ProviderName)
    let connection = factory.CreateConnection()
    connection.ConnectionString <- connectionSettings.ConnectionString;
    connection
#endif

let getQueries (req:HttpRequest)  =
    let q = match req.queryParam "queries" with
                              | Choice2Of2 e -> 1
                              | Choice1Of2 queries -> match Int32.TryParse queries with
                                                      | true, v -> v
                                                      | _ -> 1 
    Math.Max(1, Math.Min(500, q))

// http://frameworkbenchmarks.readthedocs.org/en/latest/Project-Information/Framework-Tests/#single-database-query
let dbTest : WebPart = fun ctx -> async {
    use db = new MySqlConnection(MYSQL_CONNECTION_STRING)
    db.Open()
    use dbcmd = db.CreateCommand()
    dbcmd.CommandText <- "SELECT id, randomNumber FROM world WHERE id = @id"

    // TODO (this is "raw", use Dapper as ORM)
    let getRandomWorld (db:IDbConnection) =
        let id = random.Next(0, 10000) + 1
        dbcmd.Parameters.Clear()
        dbcmd.Parameters.Add(new MySqlParameter("id", id)) |> ignore
        use reader = dbcmd.ExecuteReader()
        if reader.Read() then
            { id = reader.GetInt32(0); randomNumber = reader.GetInt32(1)}
        else
            failwith "no data"

    let q = getQueries ctx.request
    let result = 
        if q = 1 then
            getRandomWorld db |> json
        else        
            [for i = 1 to q do yield getRandomWorld db] 
            |> List.toArray // needed because F# lists fail serialization
            |> json

    return! result ctx
}

let dbUpdates : WebPart = fun ctx -> async {

    use connection = new MySqlConnection(MYSQL_CONNECTION_STRING)
    connection.Open()

    use selectCommand = connection.CreateCommand()
    use updateCommand = connection.CreateCommand()

    selectCommand.CommandText <- "SELECT * FROM world WHERE id = @ID";
    updateCommand.CommandText <- "UPDATE world SET randomNumber = @Number WHERE id = @ID";

    let q = getQueries ctx.request
    let result = 
        [for i = 1 to q do 
            let randomID = random.Next(0, 10000) + 1
            selectCommand.Parameters.Clear()
            selectCommand.Parameters.Add(new MySqlParameter("ID", randomID)) |> ignore

            // Don't use CommandBehavior.SingleRow because that will make the MySql provider
            // send two extra commands to limit the result to one row.
            use reader = selectCommand.ExecuteReader()
            let world = if reader.Read() then
                            { id = reader.GetInt32(0); randomNumber = reader.GetInt32(1)}

                        else
                            failwith "no data"
            reader.Close()

            let randomNumber = random.Next(0, 10000) + 1
            updateCommand.Parameters.Clear()
            updateCommand.Parameters.Add(new MySqlParameter("ID", randomID)) |> ignore
            updateCommand.Parameters.Add(new MySqlParameter("Number", randomNumber)) |> ignore
            if updateCommand.ExecuteNonQuery() > 0 then
                yield { world with randomNumber = randomNumber }
        ] 
        |> List.toArray // needed because F# lists fail serialization
        |> json

    return! result ctx
}

let toHtml l = 
    let sb = new StringBuilder()
    sb.Append("""<!DOCTYPE html>
<html>
<head><title>Fortunes</title></head>
<body>
<table>
<tr><th>id</th><th>message</th>""") |> ignore
    l |> Seq.iter(fun f -> 
        sb.Append("<tr><td>")
          .Append(f.Id)
          .Append("</td><td>")
          .Append(HttpUtility.HtmlEncode(f.Message))
          .Append("</td></tr>") |> ignore
    )
    sb.Append("""</table>
</body>
</html>""").ToString()

#if NEVER
let decode (str:string) =
    let bytes = Array.create (str.Length * sizeof<Char>) (new Byte()) 
    System.Buffer.BlockCopy(str.ToCharArray(), 0, bytes, 0, bytes.Length);
    Encoding.UTF8.GetString(bytes)
#endif

let fortunes : WebPart = fun ctx -> async {
    use db = new MySqlConnection(MYSQL_CONNECTION_STRING)
    db.Open()
    use dbcmd = db.CreateCommand()
#if NEVER
    dbcmd.CommandText <- "set names utf8"
    dbcmd.ExecuteNonQuery() |> ignore
#endif
    dbcmd.CommandText <- "SELECT id, message FROM fortune"
    use reader = dbcmd.ExecuteReader(CommandBehavior.SequentialAccess)
    let result = 
       [ while reader.Read() do

            yield { Id      = reader.GetInt32(0); 
                    Message = reader.GetString(1) } ]
       |> List.append [ { Id = 0; Message = "Additional fortune added at request time"} ]
       |> List.sortBy (fun f -> f.Id)

    return! OK (toHtml result) ctx
}

// the Suave web application
let app =
    choose [
        path "/json"        >>= json { message = "Hello, World!" }
        path "/db"          >>= dbTest
        path "/queries"     >>= dbTest
        path "/fortunes"    >>= Writers.setMimeType "text/html; charset=utf-8" >>= fortunes
        path "/fortunes/"   >>= Writers.setMimeType "text/html; charset=utf-8" >>= fortunes
        path "/plaintext"   >>= OK "Hello, World!"
        path "/updates"     >>= dbUpdates
        NOT_FOUND           "Found no handlers" ]

// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.
[<EntryPoint>]
let main argv = 
    let port = 8080
    let serverConfig =
        { Web.defaultConfig with homeFolder = Some __SOURCE_DIRECTORY__
                                 logger = Logging.Loggers.saneDefaultsFor Logging.LogLevel.Warn
                                 maxOps = 512
                                 bindings = [ Types.HttpBinding.mk' Types.HTTP "192.168.0.116" port ] }



    Web.startWebServer serverConfig app
    0