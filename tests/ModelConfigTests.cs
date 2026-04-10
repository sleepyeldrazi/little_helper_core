using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class ModelConfigTests
{
    [Fact]
    public void Resolve_ByModelId_SearchesAllProviders()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["provider1"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    ApiKey = "key1",
                    ApiType = "openai",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "model-a", Name = "Model A", ContextWindow = 8000 }
                    }
                }
            }
        };

        var result = config.Resolve("model-a");
        Assert.NotNull(result);
        Assert.Equal("http://localhost:11434/v1", result.BaseUrl);
        Assert.Equal("model-a", result.ModelId);
        Assert.Equal("key1", result.ApiKey);
        Assert.Equal(8000, result.ContextWindow);
    }

    [Fact]
    public void Resolve_HeadersFlowThrough()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openrouter"] = new()
                {
                    BaseUrl = "https://openrouter.ai/api/v1",
                    ApiKey = "sk-test",
                    ApiType = "openai",
                    Headers = new Dictionary<string, string>
                    {
                        ["HTTP-Referer"] = "https://example.com",
                        ["X-Title"] = "little_helper"
                    },
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "gpt-4", ContextWindow = 128000 }
                    }
                }
            }
        };

        var result = config.Resolve("gpt-4");
        Assert.NotNull(result);
        Assert.NotNull(result.Headers);
        Assert.Equal(2, result.Headers!.Count);
        Assert.Equal("https://example.com", result.Headers["HTTP-Referer"]);
        Assert.Equal("little_helper", result.Headers["X-Title"]);
    }

    [Fact]
    public void Resolve_ByProviderSlashModelId_HeadersFlowThrough()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openrouter"] = new()
                {
                    BaseUrl = "https://openrouter.ai/api/v1",
                    ApiKey = "sk-test",
                    Headers = new Dictionary<string, string>
                    {
                        ["HTTP-Referer"] = "https://example.com"
                    },
                    ApiType = "openai",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "gpt-4", ContextWindow = 128000 }
                    }
                }
            }
        };

        var result = config.Resolve("openrouter/gpt-4");
        Assert.NotNull(result);
        Assert.NotNull(result.Headers);
        Assert.Equal("https://example.com", result.Headers!["HTTP-Referer"]);
    }

    [Fact]
    public void Resolve_ByProviderSlashModelId_UsesSpecificProvider()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["ollama"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    ApiType = "openai",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "qwen3:14b", ContextWindow = 32768 }
                    }
                },
                ["openrouter"] = new()
                {
                    BaseUrl = "https://openrouter.ai/api/v1",
                    ApiKey = "sk-test",
                    ApiType = "openai",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "qwen3:14b", ContextWindow = 128000 }
                    }
                }
            }
        };

        var result = config.Resolve("openrouter/qwen3:14b");
        Assert.NotNull(result);
        Assert.Equal("https://openrouter.ai/api/v1", result.BaseUrl);
        Assert.Equal(128000, result.ContextWindow);
    }

    [Fact]
    public void Resolve_UnknownModel_ReturnsNull()
    {
        var config = new ModelConfig();
        var result = config.Resolve("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ProviderSlashUnknownModel_UsesProviderDefaults()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["ollama"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    DefaultContextWindow = 65536,
                    DefaultTemperature = 0.5,
                    ApiType = "openai",
                    Models = new List<ModelEntry>()
                }
            }
        };

        var result = config.Resolve("ollama/llama3-custom");
        Assert.NotNull(result);
        Assert.Equal("http://localhost:11434/v1", result.BaseUrl);
        Assert.Equal("llama3-custom", result.ModelId);
        Assert.Equal(65536, result.ContextWindow);
        Assert.Equal(0.5, result.Temperature);
    }

    [Fact]
    public void Resolve_ApiTypeFlowsThrough()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["anthropic-prov"] = new()
                {
                    BaseUrl = "https://api.anthropic.com",
                    ApiKey = "sk-ant-test",
                    ApiType = "anthropic",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "claude-3", ContextWindow = 200000 }
                    }
                }
            }
        };

        var result = config.Resolve("claude-3");
        Assert.NotNull(result);
        Assert.Equal("anthropic", result.ApiType);
    }

    [Fact]
    public void Resolve_AuthTypeFlowsThrough_Default()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["anthropic-prov"] = new()
                {
                    BaseUrl = "https://api.anthropic.com",
                    ApiKey = "sk-ant-test",
                    ApiType = "anthropic",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "claude-3", ContextWindow = 200000 }
                    }
                }
            }
        };

        var result = config.Resolve("claude-3");
        Assert.NotNull(result);
        Assert.Equal("x-api-key", result!.AuthType);
    }

    [Fact]
    public void Resolve_AuthTypeFlowsThrough_Bearer()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["zai"] = new()
                {
                    BaseUrl = "https://api.example.com",
                    ApiKey = "test-key",
                    ApiType = "anthropic",
                    AuthType = "bearer",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "glm-5", ContextWindow = 131072 }
                    }
                }
            }
        };

        var result = config.Resolve("glm-5");
        Assert.NotNull(result);
        Assert.Equal("bearer", result!.AuthType);
    }

    [Fact]
    public void Resolve_ToolsEnabledFlowsThrough_Null()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["local"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    ApiType = "openai",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "test-model", ContextWindow = 32768 }
                    }
                }
            }
        };

        var result = config.Resolve("test-model");
        Assert.NotNull(result);
        Assert.Null(result!.ToolsEnabled);
    }

    [Fact]
    public void Resolve_ToolsEnabledFlowsThrough_False()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["llamacpp"] = new()
                {
                    BaseUrl = "http://localhost:8080/v1",
                    ApiType = "openai",
                    ToolsEnabled = false,
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "gemma4:4b", ContextWindow = 8192 }
                    }
                }
            }
        };

        var result = config.Resolve("gemma4:4b");
        Assert.NotNull(result);
        Assert.False(result!.ToolsEnabled);
    }

    [Fact]
    public void Resolve_ToolsEnabledFlowsThrough_True()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openrouter"] = new()
                {
                    BaseUrl = "https://openrouter.ai/api/v1",
                    ApiKey = "test-key",
                    ApiType = "openai",
                    ToolsEnabled = true,
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "gpt-4", ContextWindow = 128000 }
                    }
                }
            }
        };

        var result = config.Resolve("gpt-4");
        Assert.NotNull(result);
        Assert.True(result!.ToolsEnabled);
    }

    [Fact]
    public void Resolve_DefaultApiTypeIsOpenai()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["local"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    // No ApiType specified
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "test-model", ContextWindow = 32768 }
                    }
                }
            }
        };

        var result = config.Resolve("test-model");
        Assert.NotNull(result);
        Assert.Equal("openai", result.ApiType);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lh_test_config_{Guid.NewGuid()}");
        var configPath = Path.Combine(tempDir, ".little_helper", "models.json");
        try
        {
            var config = new ModelConfig
            {
                DefaultModel = "test-model",
                Providers = new Dictionary<string, ProviderConfig>
                {
                    ["testprov"] = new()
                    {
                        BaseUrl = "http://localhost:1234/v1",
                        ApiKey = "test-key",
                        ApiType = "openai",
                        Models = new List<ModelEntry>
                        {
                            new() { Id = "test-model", ContextWindow = 4096, Temperature = 0.7 }
                        }
                    }
                }
            };

            // Save
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });
            File.WriteAllText(configPath, json);

            // Load
            var loaded = System.Text.Json.JsonSerializer.Deserialize<ModelConfig>(
                File.ReadAllText(configPath),
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
                });

            Assert.NotNull(loaded);
            Assert.Equal("test-model", loaded!.DefaultModel);
            Assert.Single(loaded.Providers);
            Assert.Equal("http://localhost:1234/v1", loaded.Providers["testprov"].BaseUrl);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAllModels_ReturnsAllModels()
    {
        var config = new ModelConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["a"] = new()
                {
                    BaseUrl = "http://a",
                    ApiType = "openai",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "m1", Name = "Model One", ContextWindow = 1000 }
                    }
                },
                ["b"] = new()
                {
                    BaseUrl = "http://b",
                    ApiType = "anthropic",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "m2", Name = "Model Two", ContextWindow = 2000 },
                        new() { Id = "m3", Name = "Model Three", ContextWindow = 3000 }
                    }
                }
            }
        };

        var models = config.GetAllModels();
        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Provider == "a" && m.ModelId == "m1");
        Assert.Contains(models, m => m.Provider == "b" && m.ModelId == "m2" && m.ApiType == "anthropic");
    }
}