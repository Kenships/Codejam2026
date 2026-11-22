using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// TrueHue > Build Layout Test Scene: copies GameScene to Assets/Scenes/LayoutTest.unity,
/// stops its GameManager from starting a game on Play, and adds a LayoutTestHarness.
/// Run it again after changing GameScene to pick the changes up.
/// </summary>
public static class LayoutTestSceneBuilder
{
    private const string SourceScene = "Assets/Scenes/GameScene.unity";
    private const string TestScene = "Assets/Scenes/LayoutTest.unity";

    [MenuItem("TrueHue/Build Layout Test Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TestScene) != null &&
            !EditorUtility.DisplayDialog(
                "Rebuild layout test scene?",
                $"{TestScene} will be replaced with a fresh copy of {SourceScene}.",
                "Rebuild", "Cancel"))
        {
            return;
        }

        AssetDatabase.DeleteAsset(TestScene);
        if (!AssetDatabase.CopyAsset(SourceScene, TestScene))
        {
            Debug.LogError($"[LayoutTest] Couldn't copy {SourceScene} to {TestScene}.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);

        // The harness drives the screens; a real game would bounce straight back to the menu
        // (no connected players)
        foreach (GameManager game in Object.FindObjectsByType<GameManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            SerializedObject so = new(game);
            SerializedProperty autoStart = so.FindProperty("startDrawingOnStart");
            if (autoStart != null)
            {
                autoStart.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        GameObject harness = new("Layout Test Harness");
        harness.AddComponent<LayoutTestHarness>();
        SceneManager.MoveGameObjectToScene(harness, scene);
        Selection.activeGameObject = harness;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"[LayoutTest] Built {TestScene}. Press Play and use the panel in the top-left " +
                  "of the Game view (or the harness's inspector buttons).");
    }

    [MenuItem("TrueHue/Open Layout Test Scene")]
    public static void Open()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TestScene) == null)
        {
            Build();
            return;
        }

        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);
    }
}
