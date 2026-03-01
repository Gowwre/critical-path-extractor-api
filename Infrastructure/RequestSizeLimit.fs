namespace CriticalPathExtractor.Infrastructure

open Microsoft.AspNetCore.Mvc.Filters
open Microsoft.AspNetCore.Http.Features
open System

[<AttributeUsage(AttributeTargets.Method)>]
type RequestSizeLimitAttribute(limitInBytes: int64) =
    inherit Attribute()
    
    member val LimitInBytes = limitInBytes
    
    interface IAuthorizationFilter with
        member this.OnAuthorization(context: AuthorizationFilterContext) =
            let feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()
            if feature <> null then
                feature.MaxRequestBodySize <- Nullable(this.LimitInBytes)
