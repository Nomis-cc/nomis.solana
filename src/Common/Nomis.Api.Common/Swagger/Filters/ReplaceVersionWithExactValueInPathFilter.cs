﻿// ------------------------------------------------------------------------------------------------------
// <copyright file="ReplaceVersionWithExactValueInPathFilter.cs" company="Nomis">
// Copyright (c) Nomis, 2023. All rights reserved.
// The Application under the MIT license. See LICENSE file in the solution root for full license information.
// </copyright>
// ------------------------------------------------------------------------------------------------------

using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Nomis.Api.Common.Swagger.Filters
{
    /// <summary>
    /// Filter for removing API from path in swagger documentation.
    /// </summary>
    public class ReplaceVersionWithExactValueInPathFilter :
        IDocumentFilter
    {
        /// <inheritdoc/>
        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            var paths = new OpenApiPaths();

            foreach ((string key, var value) in swaggerDoc.Paths)
                paths.Add(key.Replace("v{version}", swaggerDoc.Info.Version), value);

            swaggerDoc.Paths = paths;
        }
    }
}