namespace CriticalPathExtractor.Infrastructure

open System
open System.Text.Json
open System.Text.Json.Serialization
open CriticalPathExtractor.Types

type ThresholdValueConverter() =
    inherit JsonConverter<ThresholdValue>()
    
    override _.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) =
        match reader.TokenType with
        | JsonTokenType.Number ->
            ThresholdValue.Number(reader.GetDouble())
        | JsonTokenType.StartObject ->
            let mutable absolute = None
            let mutable percentage = None
            
            while reader.Read() && reader.TokenType <> JsonTokenType.EndObject do
                match reader.TokenType with
                | JsonTokenType.PropertyName ->
                    let propertyName = reader.GetString()
                    reader.Read() |> ignore
                    
                    match propertyName, reader.TokenType with
                    | "absolute", JsonTokenType.Number -> absolute <- Some(reader.GetDouble())
                    | "percentage", JsonTokenType.Number -> percentage <- Some(reader.GetDouble())
                    | _ -> ()
                | _ -> ()
            
            ThresholdValue.Object({
                absolute = absolute
                percentage = percentage
            })
        | _ ->
            failwith "Invalid threshold value format. Expected number or object."
    
    override _.Write(writer: Utf8JsonWriter, value: ThresholdValue, options: JsonSerializerOptions) =
        match value with
        | ThresholdValue.Number(n) ->
            writer.WriteNumberValue(n)
        | ThresholdValue.Object(config) ->
            writer.WriteStartObject()
            config.absolute |> Option.iter (fun v -> writer.WriteNumber("absolute", v))
            config.percentage |> Option.iter (fun v -> writer.WriteNumber("percentage", v))
            writer.WriteEndObject()
