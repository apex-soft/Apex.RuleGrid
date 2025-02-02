﻿using Swashbuckle.AspNetCore.Filters;
using System.Text.Json;

namespace Apex.RuleGrid.Models;

public class RuleApplicationRequest : IExamplesProvider<RuleApplicationRequest>
{
    public string ClassName { get; set; }
    public IList<JsonElement> Objects { get; set; }

    public RuleApplicationRequest GetExamples()
    {
        return new RuleApplicationRequest
        {
            ClassName = "AvailableFlight",
            Objects = new List<JsonElement>{
                {   JsonSerializer.Deserialize<JsonElement>(@"{
                        ""Origin"": ""THR"",
                        ""Destination"": ""MHD"",
                        ""MaxPrice"": null,
                        ""MaxPriceSet"": false,
                        ""CabinClass"": ""E""
                    }")
                },
                {
                    JsonSerializer.Deserialize<JsonElement>(@"{
                        ""Origin"": ""THR"",
                        ""Destination"": ""ISF"",
                        ""MaxPrice"": null,
                        ""MaxPriceSet"": false,
                        ""CabinClass"": ""E""
                    }")
                }
            }
        };
    }
}


