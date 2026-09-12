using VoiceChanger.Neural;
using Xunit;

namespace VoiceChanger.Tests;

public class VoiceCatalogTests
{
    [Fact]
    public void VoiceCatalog_DiscoversModelsAndIgnoresSharedAssets()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"voice_catalog_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create sample shared base models (should be ignored by GetAvailableVoices)
            File.WriteAllText(Path.Combine(tempDir, "contentvec.onnx"), "dummy_contentvec");
            File.WriteAllText(Path.Combine(tempDir, "rmvpe.onnx"), "dummy_rmvpe");

            // Create top-level model
            string topLevelOnnx = Path.Combine(tempDir, "SingerA.onnx");
            File.WriteAllText(topLevelOnnx, "dummy_model");
            File.WriteAllText(Path.ChangeExtension(topLevelOnnx, ".index"), "dummy_index");

            // Create subfolder model
            string subDir = Path.Combine(tempDir, "AnimeHero");
            Directory.CreateDirectory(subDir);
            string subOnnx = Path.Combine(subDir, "model.onnx");
            File.WriteAllText(subOnnx, "dummy_sub_model");
            File.WriteAllText(Path.Combine(subDir, "added_IVF256_Flat_Fast.index"), "dummy_sub_index");

            var catalog = new VoiceCatalog(tempDir);
            var voices = catalog.GetAvailableVoices();

            Assert.Equal(2, voices.Count);

            var topModel = Assert.Single(voices, v => v.Name == "SingerA");
            Assert.Equal(topLevelOnnx, topModel.ModelPath);
            Assert.NotNull(topModel.IndexPath);

            var folderModel = Assert.Single(voices, v => v.Name == "AnimeHero");
            Assert.Equal(subOnnx, folderModel.ModelPath);
            Assert.NotNull(folderModel.IndexPath);

            // Verify shared model paths
            Assert.Equal(Path.Combine(tempDir, "contentvec.onnx"), catalog.ContentVecModelPath);
            Assert.Equal(Path.Combine(tempDir, "rmvpe.onnx"), catalog.RmvpeModelPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void VoiceCatalog_EmptyDirectory_ReturnsEmptyList()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"voice_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var catalog = new VoiceCatalog(tempDir);
            var voices = catalog.GetAvailableVoices();
            Assert.Empty(voices);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void VoiceCatalog_DefaultModelsDirectory_PointsToAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChanger",
            "models");

        Assert.Equal(expected, VoiceCatalog.DefaultModelsDirectory);
        var catalog = new VoiceCatalog();
        Assert.Equal(expected, catalog.ModelsDirectory);
    }

    [Fact]
    public void VoiceCatalog_SetModelsDirectory_UpdatesLocationAndDiscoversNewFolder()
    {
        string dir1 = Path.Combine(Path.GetTempPath(), $"voice_cat1_{Guid.NewGuid():N}");
        string dir2 = Path.Combine(Path.GetTempPath(), $"voice_cat2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        try
        {
            File.WriteAllText(Path.Combine(dir1, "Voice1.onnx"), "dummy1");
            File.WriteAllText(Path.Combine(dir2, "Voice2.onnx"), "dummy2");

            var catalog = new VoiceCatalog(dir1);
            var voices1 = catalog.GetAvailableVoices();
            Assert.Single(voices1, v => v.Name == "Voice1");

            // Switch to dir2
            catalog.SetModelsDirectory(dir2);
            Assert.Equal(Path.GetFullPath(dir2), catalog.ModelsDirectory);

            var voices2 = catalog.GetAvailableVoices();
            Assert.Single(voices2, v => v.Name == "Voice2");

            // Reset to default
            catalog.SetModelsDirectory(null);
            Assert.Equal(VoiceCatalog.DefaultModelsDirectory, catalog.ModelsDirectory);
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, recursive: true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public void VoiceCatalog_DiscoversDeeplyNestedModelsAndHubertBase()
    {
        string rootDir = Path.Combine(Path.GetTempPath(), $"nested_vc_{Guid.NewGuid():N}");
        string modelsDir = Path.Combine(rootDir, "models");
        string baseDir = Path.Combine(modelsDir, "base");
        string voiceDir = Path.Combine(modelsDir, "Custom Speaker");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(voiceDir);

        try
        {
            File.WriteAllText(Path.Combine(baseDir, "hubert_base.onnx"), "dummy_hubert");
            File.WriteAllText(Path.Combine(baseDir, "rmvpe.onnx"), "dummy_rmvpe");
            File.WriteAllText(Path.Combine(voiceDir, "custom-speaker.onnx"), "dummy_voice");
            File.WriteAllText(Path.Combine(voiceDir, "feature_retrieval.index"), "dummy_index");

            // Point catalog to rootDir (simulating nested multi-level directory)
            var catalog = new VoiceCatalog(rootDir);

            var voices = catalog.GetAvailableVoices();
            var voice = Assert.Single(voices);
            Assert.Equal("Custom Speaker", voice.Name);
            Assert.Equal(Path.Combine(voiceDir, "custom-speaker.onnx"), voice.ModelPath);
            Assert.Equal(Path.Combine(voiceDir, "feature_retrieval.index"), voice.IndexPath);

            // Verify base model resolution
            Assert.Equal(Path.Combine(baseDir, "hubert_base.onnx"), catalog.ContentVecModelPath);
            Assert.Equal(Path.Combine(baseDir, "rmvpe.onnx"), catalog.RmvpeModelPath);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, recursive: true);
        }
    }
}
