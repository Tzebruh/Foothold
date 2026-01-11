using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Foothold;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static Scene currentScene;
    internal static Camera mainCamera;
    internal static Vector3 focalReferencePoint;
    internal static FastFrustum cameraFrustum;
    internal static bool activated = false;

    static Vector3 scale = new Vector3(0.2f, 0.2f, 0.2f);

    static List<Matrix4x4[]> standableMatrices = new();
    static List<PositionKey[]> linkedStandableMatrices = new();
    static List<Matrix4x4[]> nonStandableMatrices = new();
    static List<PositionKey[]> linkedNonStandableMatrices = new();
    static (int, int) nextFreeStandableMatrixIndex = (0, 0);
    static (int, int) nextFreeNonStandableMatrixIndex = (0, 0);

    static int cullingLayer = 0;
    static RenderParams standableRP;
    static RenderParams nonStandableRP;
    internal static Material standableMaterial;
    internal static Material nonStandableMaterial;
    internal static float alpha = 0.25f;
    internal static float baseAlpha = 0.25f;
    internal static bool continuousPaused = false;
    internal static bool continuousPauseToggleQueued = false;

    private static readonly Dictionary<(int, int), PositionYList> positionCache = new();
    
    private static readonly RaycastHit[] _terrainHitBuffer = new RaycastHit[1000];
    private static Mesh ballMesh;
    private static float lastScanTime = 0; // should only be needed when fade away is on
    private static float lastAlphaChangeTime = 0; // should only be needed when fade away is on

    private ConfigEntry<KeyCode> configActivationKey;
    private ConfigEntry<bool> configDebugMode;
    private ConfigEntry<Mode> configMode;
    private ConfigEntry<StandableColor> configStandableBallColor;
    private ConfigEntry<NonStandableColor> configNonStandableBallColor;
    private ConfigEntry<float> configScalePercent;
    private ConfigEntry<float> configAlpha;
    private ConfigEntry<float> configRange;

    private ConfigEntry<float> configXZFreq;
    private ConfigEntry<int> configMaximumPointsPerFrame;
    private ConfigEntry<bool> configDetectConcave;

    private static SafeXZ nearestGridToCamera; // nearest grid point to camera position, updated each frame
    // This allows caching of grid positions instead of recalculating each time

    private static float freq;
    private static int safeFreq;
    private static float range;
    private static int safeRange;
    private static int maximumRaysPerFrame;
    private static bool detectConcave;



    private Color LastStandableColor = Color.white;
    private Color LastNonStandableColor = Color.red;
    private static float scalePercent = 1f;

    private static bool isVisualizationRunning = false;
    
    private static string additionalDebugInfo = "";
    
    Color standable;
    Color NonStandable;
    static int totalCalls = 0;


    private void Awake()
    {
        Logger = base.Logger;

        configMode = Config.Bind("General", "Activation Mode", Mode.Continuous, """
            Toggle: Press once to activate; press again to hide the indicator.
            Fade Away: Activates every time the button is pressed. The indicator will fade away after 3 seconds. Credit to VicVoss on GitHub for the idea.
            Trigger: Activates every time the button is pressed. The indicator will remain visible.
            Continuous: Always active. The indicator will remain visible, and updates as you move. Implemented by cjmanca on GitHub.
            """);

        configStandableBallColor = Config.Bind("Appearance", "Standable ground Color", StandableColor.White, "Change the ball color of standable ground.");
        configNonStandableBallColor = Config.Bind("Appearance", "Non-standable ground Color", NonStandableColor.Red, "Change the ball color of non-standable ground.");
        configAlpha = Config.Bind("Appearance", "Transparency Percent", 100f, new ConfigDescription("How transparent the balls are.", new AcceptableValueRange<float>(1f, 100f)));


        configScalePercent = Config.Bind("Appearance", "Scale Percent", 100f, new ConfigDescription("How large the standing point indicators are.", new AcceptableValueRange<float>(1f, 200f)));

        configRange = Config.Bind("General", "Detection Range", 10f, new ConfigDescription("How far from the camera the balls are placed. Increasing will affect performance.", new AcceptableValueRange<float>(5f, 50f)));

        configXZFreq = Config.Bind("General", "Horizontal grid spacing", 0.5f, new ConfigDescription("How far apart the balls are placed horizontally. Reducing will affect performance.", new AcceptableValueRange<float>(0.1f, 2f)));

        configMaximumPointsPerFrame = Config.Bind("General", "Maximum points per frame", 100, new ConfigDescription("The maximum number of raycasts to do per frame to check for standing spots. Higher values reduce fps but make the visualization appear faster.", new AcceptableValueRange<int>(10, 20000)));

        configDetectConcave = Config.Bind("General", "Detect Concave Points", true, "Detecting standing spots on concave meshes takes many extra raycasts. Disabling won't show these standing spots, but will improve performance. Remember to increase 'Maximum points per frame' by about 10x if you enable this (1000+ at least - New default assumes this is enabled).");

        configActivationKey = Config.Bind("General", "Activation Key", KeyCode.F);
        configDebugMode = Config.Bind("Debug", "Debug Mode", false, "Show debug information");

        string assemblyFolder = Path.GetDirectoryName(new System.Uri(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).LocalPath);
        string[] possibleAssetbundlePaths = new string[]
        {
            Path.Combine(assemblyFolder, "Foothold.assetbundle"),
            Path.Combine(assemblyFolder, "Foothold.bundle"),
            Path.Combine(assemblyFolder, "Foothold.unity3d"),
            Path.Combine(assemblyFolder, "assets", "Foothold.assetbundle"),
            Path.Combine(assemblyFolder, "assets", "Foothold.bundle"),
            Path.Combine(assemblyFolder, "assets", "Foothold.unity3d"),
            Path.Combine(assemblyFolder, "Foothold"),
            Path.Combine(assemblyFolder, "assets", "Foothold"),
        };

        AssetBundle bundle = null;
        string bundlePath = null;

        foreach (var path in possibleAssetbundlePaths)
        {
            if (File.Exists(path))
            {
                bundlePath = path;
                break;
            }
        }
        if (bundlePath == null)
        {
            Logger.LogError("Failed to load AssetBundle (not found)");
            return;
        }

        bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            Logger.LogError("Failed to load AssetBundle (failed to load from file)");
            return;
        }

        Material _instancedMat = bundle.LoadAsset<Material>("Foothold");
        if (_instancedMat == null)
        {
            Logger.LogError("Failed to load instanced material from bundle");
            return;
        }
        else
        {
            Logger.LogInfo("Loaded instanced material: " + _instancedMat.name);
        }


        ballMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

        Logger.LogInfo($"mesh = {ballMesh}");


        Material mat = new(_instancedMat);
        // permanently borrowed from https://discussions.unity.com/t/how-to-make-a-urp-lit-material-semi-transparent-using-script-and-then-set-it-back-to-being-solid/942231/3
        mat.SetFloat("_Surface", 1);
        mat.SetFloat("_Blend", 0);
        mat.SetInt("_IgnoreProjector", 1);           // Ignore projectors (like rain)
        mat.SetInt("_ReceiveShadows", 0);            // Disable shadow reception
        mat.SetInt("_ZWrite", 0);                    // Disable z-writing
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        mat.enableInstancing = true;
    
        Color c = Color.white;
        c.a = baseAlpha;

        mat.color = c;
        mat.SetColor("_BaseColor", c);
        
        mat.enableInstancing = true;
        standableMaterial = mat;

        mat = mat = new(_instancedMat);
        // permanently borrowed from https://discussions.unity.com/t/how-to-make-a-urp-lit-material-semi-transparent-using-script-and-then-set-it-back-to-being-solid/942231/3
        mat.SetFloat("_Surface", 1);
        mat.SetFloat("_Blend", 0);
        mat.SetInt("_IgnoreProjector", 1);           // Ignore projectors (like rain)
        mat.SetInt("_ReceiveShadows", 0);            // Disable shadow reception
        mat.SetInt("_ZWrite", 0);                    // Disable z-writing
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;

        c = Color.magenta;
        c.a = baseAlpha;

        mat.color = c;
        mat.SetColor("_BaseColor", c);
        
        mat.enableInstancing = true;
        nonStandableMaterial = mat;

        standableRP = new RenderParams(standableMaterial) {
            layer = cullingLayer
        };
        nonStandableRP = new RenderParams(nonStandableMaterial) {
            layer = cullingLayer
        };

        SceneManager.sceneLoaded += OnSceneLoaded;

        configStandableBallColor.SettingChanged += Color_SettingChanged;
        configNonStandableBallColor.SettingChanged += Color_SettingChanged;
        configMode.SettingChanged += ConfigMode_SettingChanged;

        configRange.SettingChanged += Color_SettingChanged;
        configXZFreq.SettingChanged += Color_SettingChanged;
        configMaximumPointsPerFrame.SettingChanged += Color_SettingChanged;
        configScalePercent.SettingChanged += Color_SettingChanged;
        configDetectConcave.SettingChanged += Color_SettingChanged;
        configAlpha.SettingChanged += Color_SettingChanged;




        standableMatrices.Add(new Matrix4x4[500]);
        linkedStandableMatrices.Add(new PositionKey[500]);
        nonStandableMatrices.Add(new Matrix4x4[500]);
        linkedNonStandableMatrices.Add(new PositionKey[500]);


        Logger.LogInfo($"Loaded Foothold? version {MyPluginInfo.PLUGIN_VERSION}");
    }

    private void ConfigMode_SettingChanged(object sender, EventArgs e)
    {
        if (configMode.Value != Mode.FadeAway)
        {
            Color baseColor = standableMaterial.GetColor("_BaseColor");
            baseColor.a = baseAlpha;
            standableMaterial.SetColor("_BaseColor", baseColor);
            standableRP.material = standableMaterial;
            
            baseColor = nonStandableMaterial.GetColor("_BaseColor");
            baseColor.a = baseAlpha;
            nonStandableMaterial.SetColor("_BaseColor", baseColor);
            nonStandableRP.material = nonStandableMaterial;
        }
    }

    private void Color_SettingChanged(object sender, EventArgs e)
    {
        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            Logger.LogInfo($"Color_SettingChanged called");

            ReturnAllBallsToPool();

            freq = configXZFreq.Value;
            safeFreq = (int)Mathf.Round(freq * 10f);
            range = Mathf.Round(configRange.Value / freq) * freq; // range needs to be a multiple of frequency for the grid to step properly
            safeRange = (int)Mathf.Round(range * 10f);
            maximumRaysPerFrame = configMaximumPointsPerFrame.Value * 10;
            scalePercent = configScalePercent.Value / 100f;
            scale = Vector3.one / 10 * scalePercent;
            baseAlpha = configAlpha.Value / 100f;
            detectConcave = configDetectConcave.Value;



            if (configStandableBallColor.Value == StandableColor.Green)
            {
                standable = Color.green;
            }
            else
            {
                standable = Color.white;
            }

            if (configNonStandableBallColor.Value == NonStandableColor.Magenta)
            {
                NonStandable = Color.magenta;
            }
            else
            {
                NonStandable = Color.red;
            }

            if (LastStandableColor != standable || LastNonStandableColor != NonStandable)
            {
                ReturnAllBallsToPool();

                // reset last camera to force full redraw for continuous mode
                nearestGridToCamera.x = 0;
                nearestGridToCamera.y = 0;
                nearestGridToCamera.z = 0;
            }


            if (LastStandableColor != standable)
            {
                Color c = standable;
                c.a = baseAlpha;
                standableMaterial.color = c;
                standableMaterial.SetColor("_BaseColor", c);
                standableRP.material = standableMaterial;
                LastStandableColor = standable;
            }

            if (LastNonStandableColor != NonStandable)
            {
                Color c = NonStandable;
                c.a = baseAlpha;
                nonStandableMaterial.color = c;
                nonStandableMaterial.SetColor("_BaseColor", c);
                nonStandableRP.material = nonStandableMaterial;
                LastNonStandableColor = NonStandable;
            }
        }
    }

    static int highestCount = 0;
    static int highestBallCount = 0;

    private void OnGUI()
    {
        if (!configDebugMode.Value) return;

        int count = 0;
        int ballCount = 0;

        ballCount = nextFreeStandableMatrixIndex.Item1 * 500 + nextFreeStandableMatrixIndex.Item2;
        ballCount += nextFreeNonStandableMatrixIndex.Item1 * 500 + nextFreeNonStandableMatrixIndex.Item2;

        foreach (var yDict in positionCache.Values)
        {
            count += yDict.list.Count;
        }

        if (count > highestCount)
        {
            highestCount = count;
        }
        if (ballCount > highestBallCount)
        {
            highestBallCount = ballCount;
        }



        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("Camera Position: " + focalReferencePoint.ToString("F2"));
        GUILayout.Label("balls: " + ballCount);
        GUILayout.Label("highestBallCount: " + highestBallCount);
        GUILayout.Label("positionCache (x/z count): " + positionCache.Count);

        GUILayout.Label("positionCache (total y count): " + count);
        GUILayout.Label("positionCache (highest total y count): " + highestCount);
        GUILayout.Label("positionCache (allocations): " + PositionKey.totalAllocations);
        GUILayout.Label("positionCache (totalInUse): " + PositionKey.totalInUse);
        GUILayout.Label("positionCache (leaks): " + (PositionKey.totalAllocations - (PositionKey.PoolCount + count)));
        GUILayout.Label("Total Raycasts Last Render: " + overallTotalCallsLastRender);

        GUILayout.Label("alpha: " + alpha);
        GUILayout.Label("isVisualizationRunning: " + isVisualizationRunning.ToString());
        GUILayout.Label("additionalDebugInfo: " + additionalDebugInfo);

    }

    private void OnDestroy()
    {
        foreach (var pk in positionCache.ToList())
        {
            RemoveBallsInVerticalRay(pk.Key.Item1, pk.Key.Item2);
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        currentScene = scene;
        // Checking for mainCamera in update because it PROBABLY spawns after scene load for networking reasons but this is a complete guess

        ReturnAllBallsToPool();
        positionCache.Clear();

        standableMatrices.Clear();
        linkedStandableMatrices.Clear();
        nonStandableMatrices.Clear();
        linkedNonStandableMatrices.Clear();


        standableMatrices.Add(new Matrix4x4[500]);
        linkedStandableMatrices.Add(new PositionKey[500]);
        nonStandableMatrices.Add(new Matrix4x4[500]);
        linkedNonStandableMatrices.Add(new PositionKey[500]);


        freq = configXZFreq.Value;
        safeFreq = (int)Mathf.Round(freq * 10f);
        range = Mathf.Round(configRange.Value / freq) * freq; // range needs to be a multiple of frequency for the grid to step properly
        safeRange = (int)Mathf.Round(range * 10f);
        maximumRaysPerFrame = configMaximumPointsPerFrame.Value * 10;
        scalePercent = configScalePercent.Value / 100f;
        scale = Vector3.one / 10 * scalePercent;
        baseAlpha = configAlpha.Value / 100f;
        detectConcave = configDetectConcave.Value;


        nearestGridToCamera.x = 0;
        nearestGridToCamera.y = 0;
        nearestGridToCamera.z = 0;


        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            // make pools
            if (configStandableBallColor.Value == StandableColor.Green)
            {
                standable = Color.green;
            }
            else
            {
                standable = Color.white;
            }

            if (configNonStandableBallColor.Value == NonStandableColor.Magenta)
            {
                NonStandable = Color.magenta;
            }
            else
            {
                NonStandable = Color.red;
            }

            LastStandableColor = standable;
            LastNonStandableColor = NonStandable;
        
            Color c = standable;
            c.a = baseAlpha;

            standableMaterial.color = c;
            standableMaterial.SetColor("_BaseColor", c);
            standableRP.material = standableMaterial;

            c = NonStandable;
            c.a = baseAlpha;

            nonStandableMaterial.color = c;
            nonStandableMaterial.SetColor("_BaseColor", c);
            nonStandableRP.material = nonStandableMaterial;
        }
    }

    private void Update()
    {
        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<MainCamera>()?.GetComponent<Camera>();
                return;
            }

            if (Camera.main == null) return;    

            standableRP.camera = Camera.main;
            nonStandableRP.camera = mainCamera;

            if (!isVisualizationRunning)
            {
                Character specCharacter = MainCameraMovement.specCharacter;
                if (MainCameraMovement.IsSpectating && specCharacter != null)
                {
                    // in spectator mode, center footholds on the spectated character
                    focalReferencePoint = specCharacter.refs.ragdoll.partDict[BodypartType.Head].transform.position;
                }
                else
                {
                    // Don't use camera position in case we're using a 3rd person view mod
                    focalReferencePoint = Character.localCharacter.refs.ragdoll.partDict[BodypartType.Head].transform.position;
                }
            }

            CheckHotkeys();
            if (configMode.Value == Mode.FadeAway) SetBallAlphas();

            if (configMode.Value == Mode.Continuous)
            {
                if (continuousPauseToggleQueued && !isVisualizationRunning)
                {
                    continuousPauseToggleQueued = false;
                    continuousPaused = !continuousPaused;

                    /*

                    if (continuousPaused)
                    {
                        ReturnAllBallsToPool();
                    }
                    else
                    {
                        // reset last camera to force full redraw
                        nearestGridToCamera.x = 0;
                        nearestGridToCamera.y = 0;
                        nearestGridToCamera.z = 0;
                    }
                    */

                    // just abuse the gem message for now to indicate if we're paused or not for testing purposes
                    if (GlobalEvents.OnGemActivated != null)
                    {
                        GlobalEvents.OnGemActivated(!continuousPaused);
                    }
                }

                if (!isVisualizationRunning && !continuousPaused)
                {

                    isVisualizationRunning = true;
                    StartCoroutine(RenderChangedVisualizationCoroutine());
                }
            }

            try
            {
                for (int i = 0; i < standableMatrices.Count; i++)
                {
                    if (standableMatrices[i] == null) continue;
                    if (standableMatrices[i].Length == 0) continue;
                    if (i > nextFreeStandableMatrixIndex.Item1 || (i == nextFreeStandableMatrixIndex.Item1 && nextFreeStandableMatrixIndex.Item2 == 0))
                    {
                        break;
                    }   
                    Graphics.RenderMeshInstanced(standableRP, ballMesh, 0, standableMatrices[i]);
                }
                for (int i = 0; i < nonStandableMatrices.Count; i++)
                {
                    if (nonStandableMatrices[i] == null) continue;
                    if (nonStandableMatrices[i].Length == 0) continue;
                    if (i > nextFreeNonStandableMatrixIndex.Item1 || (i == nextFreeNonStandableMatrixIndex.Item1 && nextFreeNonStandableMatrixIndex.Item2 == 0))
                    {
                        break;
                    }   
                    Graphics.RenderMeshInstanced(nonStandableRP, ballMesh, 0, nonStandableMatrices[i]);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e);
            }
        }
    }

    // Keeping Update clean by factoring this out
    private void CheckHotkeys()
    {
        if (Input.GetKeyDown(configActivationKey.Value))
        {
            if (configMode.Value == Mode.Continuous)
            {
                continuousPauseToggleQueued = true;
                return;
            }

            if (isVisualizationRunning) return;

            if (configMode.Value == Mode.Trigger)
            {
                isVisualizationRunning = true;

                StartCoroutine(RenderVisualizationCoroutine());
                return;
            }

            if (configMode.Value == Mode.Toggle)
                activated = !activated;
            if (activated || configMode.Value == Mode.FadeAway) // The activated check is after the change, so this is checking if it has just been toggled on
            {
                isVisualizationRunning = true;

                StartCoroutine(RenderVisualizationCoroutine());
            }
            else
            {
                ReturnAllBallsToPool();
            }
        }
    }

    private void ReturnAllBallsToPool()
    {
        foreach (var pk in positionCache.ToList())
        {
            RemoveBallsInVerticalRay(pk.Key.Item1, pk.Key.Item2);
        }
    }

    private static void ReturnBallToPool(PositionKey ball)
    {
        if (!ball.ballVisible || ball.ballMatrixIndex.Item1 < 0 || ball.ballMatrixIndex.Item2 < 0) 
        {
            ball.ballVisible = false;
            ball.ballMatrixIndex.Item1 = -1;
            ball.ballMatrixIndex.Item2 = -1;
            return;
        }

        Matrix4x4 previousLastEntry = Matrix4x4.zero;
        PositionKey previousLastPositionKey = null;

        if (ball.standable)
        {
            nextFreeStandableMatrixIndex.Item2 -= 1;
            if (nextFreeStandableMatrixIndex.Item2 < 0)
            {
                nextFreeStandableMatrixIndex.Item1 -= 1;
                nextFreeStandableMatrixIndex.Item2 = 499;
            }
            if (nextFreeStandableMatrixIndex.Item1 >= 0 && nextFreeStandableMatrixIndex.Item2 >= 0)
            {
                previousLastEntry = standableMatrices[nextFreeStandableMatrixIndex.Item1][nextFreeStandableMatrixIndex.Item2];
                previousLastPositionKey = linkedStandableMatrices[nextFreeStandableMatrixIndex.Item1][nextFreeStandableMatrixIndex.Item2];
                standableMatrices[ball.ballMatrixIndex.Item1][ball.ballMatrixIndex.Item2] = previousLastEntry;
                linkedStandableMatrices[ball.ballMatrixIndex.Item1][ball.ballMatrixIndex.Item2] = previousLastPositionKey;
                previousLastPositionKey.ballMatrixIndex = (ball.ballMatrixIndex.Item1, ball.ballMatrixIndex.Item2);
            }
            if (nextFreeStandableMatrixIndex.Item1 < 0)
            {
                nextFreeStandableMatrixIndex.Item1 = 0;
                nextFreeStandableMatrixIndex.Item2 = 0;
                standableMatrices[nextFreeStandableMatrixIndex.Item1][nextFreeStandableMatrixIndex.Item2] = Matrix4x4.zero;
            }
        }
        else
        {
            nextFreeNonStandableMatrixIndex.Item2 -= 1;
            if (nextFreeNonStandableMatrixIndex.Item2 < 0)
            {
                nextFreeNonStandableMatrixIndex.Item1 -= 1;
                nextFreeNonStandableMatrixIndex.Item2 = 499;
            }
            if (nextFreeNonStandableMatrixIndex.Item1 >= 0 && nextFreeNonStandableMatrixIndex.Item2 >= 0)
            {   
                previousLastEntry = nonStandableMatrices[nextFreeNonStandableMatrixIndex.Item1][nextFreeNonStandableMatrixIndex.Item2];
                previousLastPositionKey = linkedNonStandableMatrices[nextFreeNonStandableMatrixIndex.Item1][nextFreeNonStandableMatrixIndex.Item2];
                nonStandableMatrices[ball.ballMatrixIndex.Item1][ball.ballMatrixIndex.Item2] = previousLastEntry;
                linkedNonStandableMatrices[ball.ballMatrixIndex.Item1][ball.ballMatrixIndex.Item2] = previousLastPositionKey;
                previousLastPositionKey.ballMatrixIndex = (ball.ballMatrixIndex.Item1, ball.ballMatrixIndex.Item2);
                nonStandableMatrices[nextFreeNonStandableMatrixIndex.Item1][nextFreeNonStandableMatrixIndex.Item2] = Matrix4x4.zero;
            }
            if (nextFreeNonStandableMatrixIndex.Item1 < 0)
            {
                nextFreeNonStandableMatrixIndex.Item1 = 0;
                nextFreeNonStandableMatrixIndex.Item2 = 0;
                nonStandableMatrices[nextFreeNonStandableMatrixIndex.Item1][nextFreeNonStandableMatrixIndex.Item2] = Matrix4x4.zero;
            }
        }

        ball.ballVisible = false;
        ball.ballMatrixIndex.Item1 = -1;
        ball.ballMatrixIndex.Item2 = -1;
    }


    private void PlaceBall(PositionKey positionKey)
    {
        if (positionKey.ballVisible) return;

        Matrix4x4 position = Matrix4x4.TRS(positionKey.position, Quaternion.identity, scale);
        positionKey.matrix = position;
        positionKey.ballVisible = true;


        if (positionKey.standable)
        {
            standableMatrices[nextFreeStandableMatrixIndex.Item1][nextFreeStandableMatrixIndex.Item2] = position;
            linkedStandableMatrices[nextFreeStandableMatrixIndex.Item1][nextFreeStandableMatrixIndex.Item2] = positionKey;
            positionKey.ballMatrixIndex = (nextFreeStandableMatrixIndex.Item1, nextFreeStandableMatrixIndex.Item2);

            nextFreeStandableMatrixIndex.Item2++;
            if (nextFreeStandableMatrixIndex.Item2 >= 500)
            {
                nextFreeStandableMatrixIndex.Item1++;
                nextFreeStandableMatrixIndex.Item2 = 0;
                if (standableMatrices.Count <= nextFreeStandableMatrixIndex.Item1)
                {
                    standableMatrices.Add(new Matrix4x4[500]);
                    linkedStandableMatrices.Add(new PositionKey[500]);
                }
            }
        }
        else
        {
            nonStandableMatrices[nextFreeNonStandableMatrixIndex.Item1][nextFreeNonStandableMatrixIndex.Item2] = position;
            linkedNonStandableMatrices[nextFreeNonStandableMatrixIndex.Item1][nextFreeNonStandableMatrixIndex.Item2] = positionKey;
            positionKey.ballMatrixIndex = (nextFreeNonStandableMatrixIndex.Item1, nextFreeNonStandableMatrixIndex.Item2);

            nextFreeNonStandableMatrixIndex.Item2++;
            if (nextFreeNonStandableMatrixIndex.Item2 >= 500)
            {
                nextFreeNonStandableMatrixIndex.Item1++;
                nextFreeNonStandableMatrixIndex.Item2 = 0;
                if (nonStandableMatrices.Count <= nextFreeNonStandableMatrixIndex.Item1)
                {
                    nonStandableMatrices.Add(new Matrix4x4[500]);
                    linkedNonStandableMatrices.Add(new PositionKey[500]);
                }
            }
        }
    }

    private IEnumerator RenderVisualizationCoroutine()
    {
        additionalDebugInfo = "";

        ReturnAllBallsToPool();
        lastScanTime = Time.time;

        int totalCalls = 0;

        // constrain to grid, and quantize to avoid floating point inaccuracies
        nearestGridToCamera.x = (int)Mathf.Round(Mathf.Round(focalReferencePoint.x / freq) * freq * 10f); 
        nearestGridToCamera.y = focalReferencePoint.y;
        nearestGridToCamera.z = (int)Mathf.Round(Mathf.Round(focalReferencePoint.z / freq) * freq * 10f);
        
        cameraFrustum = new FastFrustum(mainCamera, range);

        for (int x = -safeRange; x <= safeRange; x += safeFreq)
        {
            cameraFrustum.PrepX(x / 10f);
            for (int z = -safeRange; z <= safeRange; z += safeFreq)
            {
                cameraFrustum.PrepXZ(z / 10f);
                try
                {
                    CheckAndPlaceBallsInVerticalRay(nearestGridToCamera.x + x, nearestGridToCamera.y, nearestGridToCamera.z + z, false);
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                }

                if (totalCalls >= maximumRaysPerFrame)
                {
                    totalCalls = 0;
                    yield return null;
                }
            }
        }

        isVisualizationRunning = false;
    }


    int overallTotalCallsLastRender = 0;
    private IEnumerator RenderChangedVisualizationCoroutine()
    {
        additionalDebugInfo = "";
        lastScanTime = Time.time;
        totalCalls = 0;
        int overallTotalCalls = 0;
        
        var (prevMinX, prevMaxX, prevMinZ, prevMaxZ) = cameraFrustum.GetFrequencyQuantizedXZFrustumBounds(freq);

        Rect oldArea = Rect.MinMaxRect(nearestGridToCamera.x - safeRange, nearestGridToCamera.z - safeRange, nearestGridToCamera.x + safeRange, nearestGridToCamera.z + safeRange);
        Rect oldCacheKeepArea = Rect.MinMaxRect(nearestGridToCamera.x - safeRange*2, nearestGridToCamera.z - safeRange*2, nearestGridToCamera.x + safeRange*2, nearestGridToCamera.z + safeRange*2);
        Rect oldRecheckArea = Rect.MinMaxRect(prevMinX, prevMinZ, prevMaxX, prevMaxZ);

        // constrain to grid, and quantize to avoid floating point inaccuracies
        nearestGridToCamera.x = (int)Mathf.Round(Mathf.Round(focalReferencePoint.x / freq) * freq * 10f); 
        nearestGridToCamera.y = focalReferencePoint.y;
        nearestGridToCamera.z = (int)Mathf.Round(Mathf.Round(focalReferencePoint.z / freq) * freq * 10f);
        
        cameraFrustum = new FastFrustum(mainCamera, range);

        var (minX, maxX, minZ, maxZ) = cameraFrustum.GetFrequencyQuantizedXZFrustumBounds(freq);

        Rect newArea = Rect.MinMaxRect(nearestGridToCamera.x - safeRange, nearestGridToCamera.z - safeRange, nearestGridToCamera.x + safeRange, nearestGridToCamera.z + safeRange);
        Rect newCacheKeepArea = Rect.MinMaxRect(nearestGridToCamera.x - safeRange*2, nearestGridToCamera.z - safeRange*2, nearestGridToCamera.x + safeRange*2, nearestGridToCamera.z + safeRange*2);
        Rect recheckArea = Rect.MinMaxRect(minX, minZ, maxX, maxZ);

        

        var newAreas = SubtractRect(newArea, oldArea);
        var expiredCacheKeepAreas = SubtractRect(oldCacheKeepArea, newCacheKeepArea);
        var expiredRecheckAreas = SubtractRect(oldRecheckArea, recheckArea);
        //var newRecheckAreas = SubtractRect(recheckArea, oldRecheckArea);

        PositionYList pCache = null;

        foreach (var area in newAreas)
        {
            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                cameraFrustum.PrepX(x / 10f);
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    cameraFrustum.PrepXZ(z / 10f);
                    try
                    {
                        pCache = CheckCacheMiss(x, nearestGridToCamera.y, z);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }

                    if (totalCalls >= maximumRaysPerFrame)
                    {
                        overallTotalCalls += totalCalls;
                        totalCalls = 0;
                        yield return null;
                    }
                    
                    try
                    {
                        PlaceBallsInVerticalRay(pCache, nearestGridToCamera.y, true);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }

                    if (totalCalls >= maximumRaysPerFrame)
                    {
                        overallTotalCalls += totalCalls;
                        totalCalls = 0;
                        yield return null;
                    }
                }
            }
        }

        foreach (var area in expiredCacheKeepAreas)
        {
            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    try
                    {
                        RemoveBallsInVerticalRay(x, z, true);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }

                    if (totalCalls >= maximumRaysPerFrame)
                    {
                        overallTotalCalls += totalCalls;
                        totalCalls = 0;
                        yield return null;
                    }
                }
            }
        }


        foreach (var area in expiredRecheckAreas)
        {

            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    try
                    {
                        RemoveBallsInVerticalRay(x, z, false);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }

                    if (totalCalls >= maximumRaysPerFrame)
                    {
                        overallTotalCalls += totalCalls;
                        totalCalls = 0;
                        yield return null;
                    }
                }
            }
        }

        {
            Rect area = recheckArea;

            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                cameraFrustum.PrepX(x / 10f);
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    try
                    {
                        cameraFrustum.PrepXZ(z / 10f);
                    
                        pCache = CheckCacheMiss(x, nearestGridToCamera.y, z);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }

                    if (totalCalls >= maximumRaysPerFrame)
                    {
                        overallTotalCalls += totalCalls;
                        totalCalls = 0;
                        yield return null;
                    }
                
                    try
                    {
                        PlaceBallsInVerticalRay(pCache, nearestGridToCamera.y, true);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }

                    if (totalCalls >= maximumRaysPerFrame)
                    {
                        overallTotalCalls += totalCalls;
                        totalCalls = 0;
                        yield return null;
                    }
                }
            }
        }

        overallTotalCallsLastRender = overallTotalCalls;

        isVisualizationRunning = false;
    }

    public static List<Rect> SubtractRect(Rect original, Rect cut)
    {
        var result = new List<Rect>();

        // Find actual intersection (clamped to outer)
        float xMin = Mathf.Max(original.xMin, cut.xMin);
        float xMax = Mathf.Min(original.xMax, cut.xMax);
        float yMin = Mathf.Max(original.yMin, cut.yMin);
        float yMax = Mathf.Min(original.yMax, cut.yMax);

        // If there is no overlap, return the original outer
        if (xMax <= xMin || yMax <= yMin)
        {
            result.Add(original);
            return result;
        }

        Rect cutArea = Rect.MinMaxRect(xMin, yMin, xMax, yMax);

        // --- Bottom strip (below intersection), full width of outer ---
        if (cutArea.yMin > original.yMin)
        {
            Rect bottom = new Rect(
                original.xMin,
                original.yMin,
                original.width,
                cutArea.yMin - original.yMin
            );
            result.Add(bottom);
        }

        // --- Top strip (above intersection), full width of outer ---
        if (cutArea.yMax < original.yMax)
        {
            Rect top = new Rect(
                original.xMin,
                cutArea.yMax,
                original.width,
                original.yMax - cutArea.yMax
            );
            result.Add(top);
        }

        // --- Left strip (beside intersection), height of intersection only ---
        if (cutArea.xMin > original.xMin)
        {
            Rect left = new Rect(
                original.xMin,
                cutArea.yMin,
                cutArea.xMin - original.xMin,
                cutArea.height
            );
            result.Add(left);
        }

        // --- Right strip (beside intersection), height of intersection only ---
        if (cutArea.xMax < original.xMax)
        {
            Rect right = new Rect(
                cutArea.xMax,
                cutArea.yMin,
                original.xMax - cutArea.xMax,
                cutArea.height
            );
            result.Add(right);
        }

        return result;
    }



    // Not going to try to fix right now, but couple issues with this:
    // 1. Sorting in chunks means the overall list isn't fully sorted, just partially sorted in chunks - this alone negates the purpose of the sorting, since it will just look random anyway
    // 2. Yielding during the replacement phase means that the list is in an inconsistent state while being sorted (duplicate entries, missing entries, etc). 
    //    Probably not a big deal unless something else accesses the list while being sorted, but still not ideal
    // 3. The sorting and custom comparison delegate adds processing time, which may negate the perceived performance gain that sorting is attempting to gain
    //
    // A temporary fix for the inconsistency is to only yield between chunks, while working on a temporary list, 
    // but this is still not ideal, since the entire list should be sorted as a whole, not in chunks
    // A full fix would require a different sorting algorithm that can be paused and resumed, which is non-trivial
    // For now, I'm going to disable sorting, since the perceived performance gain is minimal compared to the complexity introduced
    // especially in light of the other optimizations made (full vertical rays, caching)
    IEnumerator SortWithYield(List<Vector3> list, Vector3 cameraPosition, int chunkSize)
    {
        int n = list.Count;
        int totalIterations = 0;

        // Create a custom comparison delegate
        Comparison<Vector3> compare = (a, b) =>
        {
            float distA = (a - cameraPosition).sqrMagnitude;
            float distB = (b - cameraPosition).sqrMagnitude;
            return distA.CompareTo(distB);
        };

        // Sort in chunks
        for (int i = 0; i < n; i += chunkSize)
        {
            int end = Mathf.Min(i + chunkSize, n);
            List<Vector3> chunk = list.GetRange(i, end - i);

            chunk.Sort(compare);

            // Replace the chunk in the original list
            for (int j = 0; j < chunk.Count; j++)
            {
                list[i + j] = chunk[j];
                totalIterations++;

                if (totalIterations % maximumRaysPerFrame == 0)
                {
                    yield return null;
                }
            }
        }
    }

    private void CheckAndPlaceBallsInVerticalRay(int x, float y, int z, bool onlyInFrustum)
    {
        var pCache = CheckCacheMiss(x, y, z);

        PlaceBallsInVerticalRay(pCache, y, onlyInFrustum);
    }

    
    private void PlaceBallsInVerticalRay(int x, float y, int z, bool onlyInFrustum)
    {
        if (positionCache.TryGetValue((x, z), out var yDict))
        {
            PlaceBallsInVerticalRay(yDict, y, onlyInFrustum);
        }
    }

    private void PlaceBallsInVerticalRay(PositionYList yDict, float y, bool onlyInFrustum)
    {
        foreach (var cachedKey in yDict.list.Values)
        {
            bool inFrustum;

            if (cachedKey != null)
            {
                if (onlyInFrustum)
                {
                    inFrustum = cameraFrustum.PreppedYContains(cachedKey.position.y);
                }
                else
                {
                    float yDist = Mathf.Abs(y - cachedKey.position.y);
                    inFrustum = yDist <= range;
                }

                if (!cachedKey.ballVisible && inFrustum)
                {
                    PlaceBall(cachedKey);
                }
                else if (cachedKey.ballVisible && !inFrustum)
                {
                    ReturnBallToPool(cachedKey);
                }
            }
        }
    }

    private void RemoveBallsInVerticalRay(int xIndex, int zIndex, bool expireCache = true)
    {
        if (positionCache.TryGetValue((xIndex, zIndex), out var yDict))
        {
            if (expireCache)
            {
                positionCache.Remove((xIndex, zIndex)); // clear cache for this vertical line
                yDict.ReturnToPool();
            }
            else
            {
                foreach (var cachedKey in yDict.list.Values)
                {
                    if (cachedKey != null)
                    {
                        ReturnBallToPool(cachedKey);
                    }
                }
            }
        }
    }

    private void DoRaycastRecursive(PositionYList pCache, Vector3 from, Vector3 to)
    {
        Vector3[] extraRaycastsNeeded = new Vector3[100];
        int totalExtraRaycasts = 0;

        int hitcount = Physics.RaycastNonAlloc(from, Vector3.down, _terrainHitBuffer, Vector3.Distance(from, to), HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap), QueryTriggerInteraction.UseGlobal);

        for (int i = 0; i < hitcount; i++)
        {
            totalCalls++;

            RaycastHit raycastHit = _terrainHitBuffer[i];

            if (!raycastHit.transform) continue;

            if (raycastHit.point.y < 0.5f) continue; // ignore surfaces below sea level

            CollisionModifier component = raycastHit.collider.GetComponent<CollisionModifier>();
            if (component && !component.standable) continue;

            if (detectConcave && totalExtraRaycasts < 100)
            {
                // detect if we need to do extra raycasts to fill in gaps due to concave surfaces
                MeshCollider mc = raycastHit.collider as MeshCollider;
                if (mc && !mc.convex)
                {
                    extraRaycastsNeeded[totalExtraRaycasts] = raycastHit.point;
                    totalExtraRaycasts++;
                }
            }
            
            if (pCache.list.ContainsKey((int)Mathf.Round(raycastHit.point.y * 10f))) continue;

            float angle = Vector3.Angle(Vector3.up, raycastHit.normal);

            if (angle < 30f) continue; // don't need to show on flat ground

            PositionKey positionKey = PositionKey.GetNew(
                raycastHit.point.x,
                raycastHit.point.y,
                raycastHit.point.z,
                angle < 50f
            );
            
            pCache.list.Add((int)Mathf.Round(raycastHit.point.y * 10f), positionKey);
        }

        if (detectConcave)
        {
            // do extra raycasts for concave surfaces
            for (int i = 0; i < totalExtraRaycasts; i++)
            {
                var line = extraRaycastsNeeded[i];

                float closestY = to.y;
                PositionKey closestPoint = null;

                foreach (var position in pCache.list.Values)
                {
                    if (position != null)
                    {
                        // top down, so higher y is closer to start point
                        if (position.position.y > closestY && position.position.y < line.y)
                        {
                            closestY = position.position.y;
                            closestPoint = position;
                        }
                    }
                }

                if (closestPoint != null)
                {
                    Vector3 newFrom = line + Vector3.down * 0.5f;
                    Vector3 newTo = closestPoint.position + Vector3.up * 0.1f;

                    if (newTo.y >= newFrom.y) continue; // sanity check

                    // start new raycast from just below the startpoint to just above the next point below it
                    DoRaycastRecursive(pCache, newFrom, newTo);
                    continue;
                }
            }
        }
    }

    private PositionYList CheckCacheMiss(int x, float y, int z)
    {
        PositionYList pCache = null;

        // cache miss, need to do raycasts to build cache for this vertical line
        if (!positionCache.TryGetValue((x, z), out pCache) || pCache == null || pCache.rayTop < y + range || pCache.rayBottom > y - range)
        {
            if (pCache != null)
            {
                positionCache.Remove((x, z));
                pCache.ReturnToPool();
            }

            pCache = PositionYList.GetNew();

            // Raycast downwards from the position to check for terrain.
            // Do a full vertical line, and cache all results for this (x,z) position
            // Stop the line just before 0 y, since we don't need to be visualizing the shallow water floor
            // Remember that x/z are increased in magnitude for index purposes
            Vector3 from = new Vector3(x/10f, y + range * 1.5f, z/10f);
            Vector3 to = from + Vector3.down * range * 3f;

            pCache.rayTop = from.y;
            pCache.rayBottom = to.y;

            DoRaycastRecursive(pCache, from, to);

            positionCache[(x, z)] = pCache;
        }
        return pCache;
    }

    // This method is dedicated to VicVoss
    private void SetBallAlphas()
    {
        if (configMode.Value != Mode.FadeAway) return; // this shouldn't be needed but it's good to be safe

        // effectively restrict framerate to 20 for performance
        if (Time.time - lastAlphaChangeTime < 0.05) return;
        lastAlphaChangeTime = Time.time;

        alpha = Mathf.Lerp(baseAlpha, 0f, Mathf.Clamp01((Time.time - (lastScanTime + 3)) / 3));


        Color baseColor = standableMaterial.GetColor("_BaseColor");
        baseColor.a = alpha;
        standableMaterial.SetColor("_BaseColor", baseColor);
        standableRP.material = standableMaterial;
        
        baseColor = nonStandableMaterial.GetColor("_BaseColor");
        baseColor.a = alpha;
        nonStandableMaterial.SetColor("_BaseColor", baseColor);
        nonStandableRP.material = nonStandableMaterial;
        

        if (alpha <= 0)
        {
            ReturnAllBallsToPool();
        }
    }

    internal enum StandableColor
    {
        White,
        Green
    }

    internal enum NonStandableColor
    {
        Red,
        Magenta
    }
    
    internal enum Mode
    {
        Toggle,
        FadeAway,
        Trigger,
        Continuous
    }

    
    class PositionKey
    {
        private static readonly Queue<PositionKey> pool = new Queue<PositionKey>();
        public static int PoolCount => pool.Count;
        private static int poolSize = 40000;
        public static int totalAllocations = 0;
        public static int totalInUse = 0;


        private bool isInUse = false;

        public Vector3 position;
        public bool standable;
        public Matrix4x4 matrix;
        public bool ballVisible = false;
        public (int, int) ballMatrixIndex = (-1, -1);

        
        static PositionKey()
        {
            for (int i = 0; i < poolSize; i++)
            {
                pool.Enqueue(new PositionKey());
                totalAllocations++;
            }
        }

        
        private PositionKey()
        {
            
        }

        public static PositionKey GetNew(float x, float y, float z, bool standable, GameObject ballObject = null)
        {
            if (pool.Count <= 0)
            {
                for (int i = 0; i < poolSize / 5; i++)
                {
                    pool.Enqueue(new PositionKey());
                    totalAllocations++;
                }
            }
            PositionKey drop = pool.Dequeue();
            drop.position = new Vector3(x, y, z);
            drop.matrix = Matrix4x4.TRS(drop.position, Quaternion.identity, scale);
            drop.standable = standable;
            drop.ballVisible = false;
            drop.isInUse = true;
            totalInUse++;
            return drop;

        }

        public static PositionKey GetNew(Vector3 position, bool standable, GameObject ballObject = null)
        {
            if (pool.Count <= 0)
            {
                for (int i = 0; i < poolSize / 5; i++)
                {
                    pool.Enqueue(new PositionKey());
                    totalAllocations++;
                }
            }
            PositionKey drop = pool.Dequeue();
            drop.position = position;
            drop.matrix = Matrix4x4.TRS(drop.position, Quaternion.identity, scale);
            drop.standable = standable;
            drop.ballVisible = false;
            drop.isInUse = true;
            totalInUse++;
            return drop;
        }

        public void ReturnToPool()
        {
            if (isInUse == false) return;

            ReturnBallToPool(this);
            position = Vector3.zero;
            standable = false;
            ballVisible = false;
            isInUse = false;
            pool.Enqueue(this);
            totalInUse--;
        }
    }

    class PositionYList
    {
        private static readonly Queue< PositionYList > pool = new Queue< PositionYList >();
        public static int PoolCount => pool.Count;
        private static int poolSize = 10000;
        public static int totalAllocations = 0;
        public static int totalInUse = 0;



        public readonly Dictionary<int, PositionKey> list = new();
        private bool isInUse = false;
        public float rayTop = 0;
        public float rayBottom = 0;

        
        static PositionYList()
        {
            for (int i = 0; i < poolSize; i++)
            {
                pool.Enqueue(new PositionYList());
                totalAllocations++;
            }
        }

        
        private PositionYList()
        {
            
        }

        public static PositionYList GetNew()
        {
            if (pool.Count <= 0)
            {
                for (int i = 0; i < poolSize / 5; i++)
                {
                    pool.Enqueue(new PositionYList());
                    totalAllocations++;
                }
            }
            PositionYList drop = pool.Dequeue();
            drop.list.Clear();
            drop.isInUse = true;
            totalInUse++;
            return drop;

        }

        public void ReturnToPool()
        {
            if (isInUse == false) return;

            totalInUse--;
            isInUse = false;

            foreach (var pk in list.Values)
            {
                if (pk != null)
                {
                    pk.ReturnToPool();
                }
            }

            list.Clear();
            pool.Enqueue(this);
        }
    }

    public struct FastFrustum
    {
        public Vector3 camPos;
        public Vector3 forward;
        public Vector3 right;
        public Vector3 up;

        public float near;
        public float far;

        public float nearSqr;
        public float farSqr;


        public float tanHalfVertFov;
        public float tanHalfHorFov;

        public FastFrustum(Camera cam, float farClipPlaneOverride = -1f)
        {
            Transform t = cam.transform;

            camPos   = t.position;
            forward  = t.forward;
            right    = t.right;
            up       = t.up;

            near = cam.nearClipPlane;
            far  = cam.farClipPlane;

            nearSqr = near * near;
            farSqr  = far * far;

            if (farClipPlaneOverride > 0f)
            {
                far  = farClipPlaneOverride;
            }

            float halfVertRad = 0.5f * cam.fieldOfView * Mathf.Deg2Rad;
            tanHalfVertFov = Mathf.Tan(halfVertRad);
            tanHalfHorFov  = tanHalfVertFov * cam.aspect;
        }

        float XPrepZ = 0f;
        float XZPrepZ = 0f;
        float XPrepX = 0f;
        float XZPrepX = 0f;
        float XPrepY = 0f;
        float XZPrepY = 0f;

        float XPrepDistance = 0f;
        float XZPrepDistance = 0f;

        public void PrepX(float x)
        {
            x = x - camPos.x;
            XPrepZ = x * forward.x;
            XPrepX = x * right.x;
            XPrepY = x * up.x;

            XPrepDistance = x * x;
        }

        public void PrepXZ(float z)
        {
            z = z - camPos.z;
            XZPrepZ = XPrepZ + (z * forward.z);
            XZPrepX = XPrepX + (z * right.z);
            XZPrepY = XPrepY + (z * up.z);

            XZPrepDistance = XPrepDistance + (z * z);
        }

        public bool PreppedYContains(float pointY)
        {
            pointY = pointY - camPos.y;

            // Approximate distance along camera forward. The edges of the camera will show further than the center, but faster
            // We need z for other calcs anyway, so may as well use it to short circuit before doing the true distance check later.
            float z = XZPrepZ + (pointY * forward.y);
            if (z < near || z > far)
                return false;
            
            // Offsets in camera's right / up directions
            float x = XZPrepX + (pointY * right.y);
            float maxX = z * tanHalfHorFov;
            if (Mathf.Abs(x) > maxX) return false;

            float y = XZPrepY + (pointY * up.y);
            float maxY = z * tanHalfVertFov;

            if (Mathf.Abs(y) > maxY) return false;

            // True pythagoras distance from camera
            float distSqr = XZPrepDistance + (pointY * pointY);
            if (distSqr < nearSqr || distSqr > farSqr)
                return false;

            return true;
        }

        public bool Contains(Vector3 point)
        {
            // Vector from camera to point
            Vector3 v = point - camPos;

            // Distance along camera forward
            float z = Vector3.Dot(v, forward);
            if (z < near || z > far)
                return false;

            // Offsets in camera's right / up directions
            float x = Vector3.Dot(v, right);
            float maxX = z * tanHalfHorFov;
            if (Mathf.Abs(x) > maxX) return false;

            float y = Vector3.Dot(v, up);
            float maxY = z * tanHalfVertFov;

            if (Mathf.Abs(y) > maxY) return false;

            // True pythagoras distance from camera
            float distSqr = v.sqrMagnitude;
            if (distSqr < nearSqr || distSqr > farSqr)
                return false;

            return true;
        }

        /// <summary>
        /// Gets the grid restricted quantized axis-aligned bounding box of the frustum projected onto the XZ plane.
        /// </summary>
        /// <returns>ValueTuple(int minX, int maxX, int minZ, int maxZ)</returns>
        public (int, int, int, int) GetFrequencyQuantizedXZFrustumBounds(float freq)
        {
            var (minX, maxX, minZ, maxZ) = GetXZFrustumBounds();
            int qMinX = (int)Mathf.Round(Mathf.Round(minX / freq) * freq * 10f);
            int qMaxX = (int)Mathf.Round(Mathf.Round(maxX / freq) * freq * 10f);
            int qMinZ = (int)Mathf.Round(Mathf.Round(minZ / freq) * freq * 10f);
            int qMaxZ = (int)Mathf.Round(Mathf.Round(maxZ / freq) * freq * 10f);

            return (qMinX, qMaxX, qMinZ, qMaxZ);
        }


        /// <summary>
        /// Gets the axis-aligned bounding box of the frustum projected onto the XZ plane.
        /// </summary>
        /// <returns>ValueTuple(float minX, float maxX, float minZ, float maxZ)</returns>
        public (float, float, float, float) GetXZFrustumBounds()
        {
            float minX;
            float maxX;
            float minZ;
            float maxZ;

            // Frustum dimensions on near/far planes
            float nearHalfHeight = near * tanHalfVertFov;
            float nearHalfWidth  = near * tanHalfHorFov;

            float farHalfHeight  = far * tanHalfVertFov;
            float farHalfWidth   = far * tanHalfHorFov;

            // Centers of near and far planes
            Vector3 centerNear = camPos + forward * near;
            Vector3 centerFar  = camPos + forward * far;

            // 4 corners of near plane
            Vector3 nearTL = centerNear + up * nearHalfHeight - right * nearHalfWidth;
            Vector3 nearTR = centerNear + up * nearHalfHeight + right * nearHalfWidth;
            Vector3 nearBL = centerNear - up * nearHalfHeight - right * nearHalfWidth;
            Vector3 nearBR = centerNear - up * nearHalfHeight + right * nearHalfWidth;

            // 4 corners of far plane
            Vector3 farTL  = centerFar + up * farHalfHeight - right * farHalfWidth;
            Vector3 farTR  = centerFar + up * farHalfHeight + right * farHalfWidth;
            Vector3 farBL  = centerFar - up * farHalfHeight - right * farHalfWidth;
            Vector3 farBR  = centerFar - up * farHalfHeight + right * farHalfWidth;

            minX = float.PositiveInfinity;
            maxX = float.NegativeInfinity;
            minZ = float.PositiveInfinity;
            maxZ = float.NegativeInfinity;

            void Encapsulate(Vector3 p)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            Encapsulate(nearTL);
            Encapsulate(nearTR);
            Encapsulate(nearBL);
            Encapsulate(nearBR);

            Encapsulate(farTL);
            Encapsulate(farTR);
            Encapsulate(farBL);
            Encapsulate(farBR);

            return (minX, maxX, minZ, maxZ);
        }
    }

    struct SafeXZ
    {
        public int x;
        public float y;
        public int z;
    }

}
