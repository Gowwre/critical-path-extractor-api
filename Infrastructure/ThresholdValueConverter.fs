namespace CriticalPathExtractor.Infrastructure

open System
open System.Text.Json
open System.Text.Json.Serialization
open CriticalPathExtractor.Types

module private ThresholdParser =
    let private applyProperty
        (propertyName: string)
        (reader: byref<Utf8JsonReader>)
        (absolute: float option)
        (percentage: float option)
        : float option * float option =
        match propertyName, reader.TokenType with
        | "absolute", JsonTokenType.Number -> Some(reader.GetDouble()), percentage
        | "percentage", JsonTokenType.Number -> absolute, Some(reader.GetDouble())
        | _ -> absolute, percentage

    let rec private readProperties
        (reader: byref<Utf8JsonReader>)
        (absolute: float option)
        (percentage: float option)
        : float option * float option =
        match reader.Read() with
        | false ->
            absolute, percentage
        | true ->
            match reader.TokenType with
            | JsonTokenType.EndObject ->
                absolute, percentage
            | JsonTokenType.PropertyName ->
                let propertyName = reader.GetString()

                match reader.Read() with
                | true ->
                    let nextAbsolute, nextPercentage = applyProperty propertyName &reader absolute percentage
                    readProperties &reader nextAbsolute nextPercentage
                | false ->
                    absolute, percentage
            | _ ->
                readProperties &reader absolute percentage

    let readThresholdObject (reader: byref<Utf8JsonReader>) : NearCriticalThreshold =
        let absolute, percentage = readProperties &reader None None
        { absolute = absolute
          percentage = percentage }

type ThresholdValueConverter() =
    inherit JsonConverter<ThresholdValue>()

    override _.Read(reader: byref<Utf8JsonReader>, _typeToConvert: Type, _options: JsonSerializerOptions) =
        match reader.TokenType with
        | JsonTokenType.Number ->
            ThresholdValue.Number(reader.GetDouble())
        | JsonTokenType.StartObject ->
            ThresholdParser.readThresholdObject &reader
            |> ThresholdValue.Object
        | _ ->
            failwith "Invalid threshold value format. Expected number or object."

    override _.Write(writer: Utf8JsonWriter, value: ThresholdValue, _options: JsonSerializerOptions) =
        match value with
        | ThresholdValue.Number(numberValue) ->
            writer.WriteNumberValue(numberValue)
        | ThresholdValue.Object(config) ->
            writer.WriteStartObject()
            config.absolute |> Option.iter (fun value -> writer.WriteNumber("absolute", value))
            config.percentage |> Option.iter (fun value -> writer.WriteNumber("percentage", value))
            writer.WriteEndObject()