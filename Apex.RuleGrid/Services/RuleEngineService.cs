﻿using Apex.RuleGrid.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Apex.RuleGrid.Exceptions;
using System.Text.Json;

namespace Apex.RuleGrid.Services
{

    public class RuleEngineService
    {
        private readonly MongoDbService _dbService;
        private readonly ReteNetwork _reteNetwork;

        public RuleEngineService(MongoDbService dbService)
        {
            _dbService = dbService;
            _reteNetwork = new ReteNetwork();
        }
        private static string ConvertExcelToJson(XLWorkbook workbook)
        {
            var result = new Dictionary<string, object>();

            foreach (var worksheet in workbook.Worksheets)
            {
                foreach (var table in worksheet.Tables)
                {
                    var headers = table.Fields.Select(f => f.Name).ToList();
                    var tableData = table.DataRange.Rows()
                        .Select(row => headers
                            .Select((header, index) => new { header, value = row.Cell(index + 1).Value.ToString() })
                            .ToDictionary(item => item.header, item => (object)item.value))
                        .ToList();

                    result[worksheet.Name] = worksheet.Name == "Metadata" ? tableData.FirstOrDefault() : tableData;
                }
            }

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }

        private static RuleSetDbModel ConvertToDbModel(string inputJson)
        {
            using var document = JsonDocument.Parse(inputJson);
            var root = document.RootElement;

            var options = new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                WriteIndented = true
            };

            var output = new RuleSetDbModel
            {
                Metadata = JsonSerializer.Deserialize<MetaData>(root.GetProperty("Metadata").GetRawText(), options),
                Rules = root.GetProperty("Rules").EnumerateArray().Select(rule => new Rule
                {
                    Index = rule.GetProperty("Index").GetString(),
                    Conditions = rule.EnumerateObject()
                                   .Where(p => p.Name.StartsWith("Condition_"))
                                   .ToDictionary(p => p.Name, p => p.Value.GetString()),
                    Actions = rule.EnumerateObject()
                                .Where(p => p.Name.StartsWith("Action_"))
                                .ToDictionary(p => p.Name, p => p.Value.GetString())
                }).ToList()
            };
            output.Rules = output.Rules.Where(x => x.Actions.Any(a => string.IsNullOrWhiteSpace(a.Value) == false)).ToList();
            var index = 1;
            foreach (var item in output.Rules.SkipWhile(x => x.Index?.Contains("#") is true))
            {
                item.Index = index.ToString();
                index++;
            }
            return output;
        }

        public async Task UploadRuleSet([FromForm] IList<IFormFile> files)
        {
            foreach (var file in files)
            {
                if (file == null || file.Length == 0)
                    throw new RuleGridException("Invalid file.");

                using var stream = file.OpenReadStream();
                using var workbook = new XLWorkbook(stream);
                var json = ConvertExcelToJson(workbook);
                var dbModel = ConvertToDbModel(json);
                _reteNetwork.AddRuleSet(dbModel);
                await _dbService.SaveJsonAsync(dbModel);
            }
        }

        public async Task<IList<JsonElement>> ApplyRulesWithRete(RuleApplicationRequest request)
        {
            var ruleSets = await _dbService.GetRulesAsync(request.ClassName);
            if (!ruleSets.Any()) return request.Objects;

            var results = new List<JsonElement>();

            foreach (var ruleSet in ruleSets)
            {
                _reteNetwork.AddRuleSet(ruleSet);

                foreach (var obj in request.Objects)
                {
                    var jsonObject = JsonSerializer.Deserialize<Dictionary<string, object>>(obj.GetRawText());
                    if (jsonObject == null) continue;

                    var matchedRules = _reteNetwork.Match(jsonObject, ruleSet.Metadata.ConditionsOperator);
                    foreach (var rule in matchedRules)
                    {
                        ApplyActions(ruleSet, rule, jsonObject, obj);
                    }

                    results.Add(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(jsonObject)));
                }
            }

            return results;
        }


        public async Task<IList<JsonElement>> ApplyRules([FromBody] RuleApplicationRequest request)
        {
            var ruleSets = await _dbService.GetRulesAsync(request.ClassName);
            if (!ruleSets.Any())
                return request.Objects;

            var results = new List<JsonElement>();

            foreach (var ruleSet in ruleSets)
            {
                var filteredRules = ruleSet.Rules.Where(r => r.Index?.Contains("#") == false);

                foreach (var obj in request.Objects)
                {
                    var jsonObject = JsonSerializer.Deserialize<Dictionary<string, object>>(obj.GetRawText());
                    if (jsonObject == null) continue;

                    foreach (var rule in filteredRules)
                    {
                        if (EvaluateConditions(ruleSet, rule, jsonObject, obj))
                        {
                            ApplyActions(ruleSet, rule, jsonObject, obj);
                        }
                    }

                    results.Add(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(jsonObject)));
                }
            }

            return results;
        }

        private bool EvaluateConditions(RuleSetDbModel ruleSet, Rule rule, Dictionary<string, object> jsonObject, JsonElement obj)
        {
            var conditionsOperator = ruleSet.Metadata.ConditionsOperator;
            var conditionMet = conditionsOperator == "AND";

            foreach (var condition in rule.Conditions)
            {
                var fieldName = RuleSetDbModel.GetRuleField(ruleSet.Rules, condition.Key, "#FieldName");
                if (string.IsNullOrWhiteSpace(fieldName) || !obj.TryGetProperty(fieldName, out var prop))
                {
                    conditionMet = false;
                    continue;
                }

                var operatorPhrase = RuleSetDbModel.GetRuleField(ruleSet.Rules, condition.Key, "#Operator");
                var conditionValue = condition.Value;

                conditionMet = conditionsOperator == "AND"
                    ? conditionMet && Rule.EvaluateCondition(operatorPhrase, prop, conditionValue)
                    : conditionMet || Rule.EvaluateCondition(operatorPhrase, prop, conditionValue);
            }

            return conditionMet;
        }

        private void ApplyActions(RuleSetDbModel ruleSet, Rule rule, Dictionary<string, object> jsonObject, JsonElement obj)
        {
            foreach (var action in rule.Actions)
            {
                var fieldName = RuleSetDbModel.GetRuleField(ruleSet.Rules, action.Key, "#FieldName");
                if (string.IsNullOrWhiteSpace(fieldName) || !obj.TryGetProperty(fieldName, out var prop))
                    continue;

                var operatorPhrase = RuleSetDbModel.GetRuleField(ruleSet.Rules, action.Key, "#Operator");
                var actionValue = action.Value;

                switch (operatorPhrase)
                {
                    case "Set":
                        jsonObject[fieldName] = ParseValue(prop, actionValue);
                        break;
                    case "Increase":
                        jsonObject[fieldName] = long.Parse(prop.GetRawText()) + long.Parse(actionValue);
                        break;
                    case "Decrease":
                        jsonObject[fieldName] = long.Parse(prop.GetRawText()) - long.Parse(actionValue);
                        break;
                }

                if (ruleSet.Metadata.GeneralAction == "SetAppliedRules")
                {
                    List<string> appliedRules = [];
                    if (jsonObject.ContainsKey("AppliedRules"))
                    {
                        appliedRules = jsonObject["AppliedRules"] as List<string>;
                    }
                    appliedRules.Add($"RuleId:{ruleSet.Metadata.Id} RuleIndex:{rule.Index}");
                    jsonObject["AppliedRules"] = appliedRules.Distinct().ToList();
                }
            }
        }

        private static object ParseValue(JsonElement prop, string value)
        {
            return prop.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => bool.Parse(value),
                _ => value
            };
        }
    }
}
