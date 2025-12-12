using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Foothold;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static Scene currentScene;
    internal static MainCamera mainCamera;
    internal static bool activated = false;
    internal static Material baseMaterial;
    internal static float alpha = 1;


    private static readonly Dictionary<(int, int), PositionYList> positionCache = new();
    
    private static readonly RaycastHit[] _terrainHitBuffer = new RaycastHit[1000];

    private static readonly Queue<GameObject> pool_balls = [];
    private static readonly Queue<GameObject> pool_redBalls = [];
    private static float lastScanTime = 0; // should only be needed when fade away is on
    private static float lastAlphaChangeTime = 0; // should only be needed when fade away is on

    private ConfigEntry<KeyCode> configActivationKey;
    private ConfigEntry<bool> configDebugMode;
    private ConfigEntry<Mode> configMode;
    private ConfigEntry<StandableColor> configStandableBallColor;
    private ConfigEntry<NonStandableColor> configNonStandableBallColor;
    private ConfigEntry<float> configRange;

    private ConfigEntry<float> configXZFreq;
    private ConfigEntry<int> configMaximumPointsPerFrame;

    private static SafeXZ nearestGridToCamera; // nearest grid point to camera position, updated each frame
    // This allows caching of grid positions instead of recalculating each time

    private static float freq;
    private static int safeFreq;
    private static float range;
    private static int safeRange;
    private static int maximumRaysPerFrame;



    private Color LastStandableColor = Color.white;
    private Color LastNonStandableColor = Color.red;

    private static int poolSize = 3000; 
    private static bool isVisualizationRunning = false;
    
    private static string additionalDebugInfo = "";
    
    Color standable;
    Color NonStandable;
    static int totalCalls = 0;

    /*
     * Yield thresholds for coroutine execution to prevent frame drops.
     *
     * MainYield is used during the preprocessing phase:
     * - Iterates through the 3D grid and separates positions into [visiblePositions] and [nonVisiblePositions].
     * - Sorts both lists by distance to the camera.
     * - This is a lightweight operation and should complete within 5 frames.
     *
     * PlaceYield is used during the ball placement phase:
     * - Processes each position by calling CheckAndPlaceBallAt.
     * - Prioritizes [visiblePositions] first, then [nonVisiblePositions].
     * - This is a heavier operation, as it involves a RaycastHit for each of the 35,281 positions.
     *
     * The yield values are chosen to balance performance and responsiveness:
     * - A higher yield value allows more work per frame but increases the risk of frame drops.
     * - At 120 FPS, the current setting results in less than 5 FPS drop, which is negligible.
     * - In laggy scenarios (e.g., 20 FPS), the coroutine should still complete in under 2 seconds without noticeable impact.
     */

    private void Awake()
    {
        Logger = base.Logger;

        configStandableBallColor = Config.Bind("General", "Standable ground Color", StandableColor.White, "Change the ball color of standable ground.");
        configNonStandableBallColor = Config.Bind("General", "Non-standable ground Color", NonStandableColor.Red, "Change the ball color of non-standable ground.");

        configRange = Config.Bind("General", "Detection Range", 10f, "How far from the camera the balls are placed. Increasing will heavily affect performance.");

        configXZFreq = Config.Bind("General", "Horizontal grid spacing", 0.5f, "How far apart the balls are placed horizontally. Reducing will heavily affect performance.");

        configMaximumPointsPerFrame = Config.Bind("General", "Maximum points per frame", 100, "The maximum number of points to place per frame. Higher values increase performance impact but make the visualization appear faster.");

        configActivationKey = Config.Bind("General", "Activation Key", KeyCode.F);

        configMode = Config.Bind("General", "Activation Mode", Mode.FadeAway, """
            Toggle: Press once to activate; press again to hide the indicator.
            Fade Away: Activates every time the button is pressed. The indicator will fade away after 3 seconds. Credit to VicVoss on GitHub for the idea.
            Trigger: Activates every time the button is pressed. The indicator will remain visible.
            Continuous: Always active. The indicator will remain visible, and updates as you move.
            """);
        configDebugMode = Config.Bind("General", "Debug Mode", false, "Show debug information");

        Material mat = new(Shader.Find("Universal Render Pipeline/Lit"));
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
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        baseMaterial = mat;

        SceneManager.sceneLoaded += OnSceneLoaded;

        configStandableBallColor.SettingChanged += Color_SettingChanged;
        configNonStandableBallColor.SettingChanged += Color_SettingChanged;
        configMode.SettingChanged += ConfigMode_SettingChanged;

        configRange.SettingChanged += Color_SettingChanged;
        configXZFreq.SettingChanged += Color_SettingChanged;
        configMaximumPointsPerFrame.SettingChanged += Color_SettingChanged;



        Logger.LogInfo($"Loaded Foothold? version {MyPluginInfo.PLUGIN_VERSION}");
    }

    private void ConfigMode_SettingChanged(object sender, EventArgs e)
    {
        ReturnAllBallsToPool();

        if (configMode.Value != Mode.FadeAway)
        {
            foreach (var pkc in positionCache.Values)
            {
                foreach (var ball in pkc.list.Values)
                {
                    if (ball != null)
                    {
                        Material mat = ball.ballObject.GetComponent<Renderer>().material;
                        Color baseColor = mat.GetColor("_BaseColor");
                        baseColor.a = alpha;
                        mat.SetColor("_BaseColor", baseColor);
                    }
                }
            }
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
            maximumRaysPerFrame = configMaximumPointsPerFrame.Value;

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

            poolSize = (int)((2 * range / freq) * (2 * range / freq)) * 4; // adjust pool size based on range and frequency

            if (LastStandableColor != standable)
            {
                pool_balls.Clear();
                for (int i = 0; i < poolSize; i++)
                {
                    pool_balls.Enqueue(CreateBall(standable));
                }
                LastStandableColor = standable;
            }

            if (LastNonStandableColor != NonStandable)
            {
                pool_redBalls.Clear();
                for (int i = 0; i < poolSize; i++)
                {
                    pool_redBalls.Enqueue(CreateBall(NonStandable));
                }
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
        foreach (var yDict in positionCache.Values)
        {
            count += yDict.list.Count;
            foreach (var pk in yDict.list.Values)
            {
                if (pk != null && pk.ballObject != null)
                {
                    ballCount++;
                }
            }
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
        GUILayout.Label("Camera Position: " + mainCamera?.transform.position.ToString("F2"));
        GUILayout.Label("balls: " + ballCount);
        GUILayout.Label("highestBallCount: " + highestBallCount);
        GUILayout.Label("pool_balls: " + pool_balls.Count);
        GUILayout.Label("pool_redBalls: " + pool_redBalls.Count);
        GUILayout.Label("positionCache (x/z count): " + positionCache.Count);


        GUILayout.Label("positionCache (total y count): " + count);
        GUILayout.Label("positionCache (highest total y count): " + highestCount);
        GUILayout.Label("positionCache (allocations): " + PositionKey.totalAllocations);
        GUILayout.Label("positionCache (totalInUse): " + PositionKey.totalInUse);
        GUILayout.Label("positionCache (leaks): " + (PositionKey.totalAllocations - (PositionKey.PoolCount + count)));

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
        
        pool_balls.Clear();
        pool_redBalls.Clear();
        positionCache.Clear();


        freq = configXZFreq.Value;
        safeFreq = (int)Mathf.Round(freq * 10f);
        range = Mathf.Round(configRange.Value / freq) * freq; // range needs to be a multiple of frequency for the grid to step properly
        safeRange = (int)Mathf.Round(range * 10f);
        maximumRaysPerFrame = configMaximumPointsPerFrame.Value;


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

            poolSize = (int)((2 * range / freq) * (2 * range / freq)) * 4; // adjust pool size based on range and frequency

            for (int i = 0; i < poolSize; i++)
            {
                pool_balls.Enqueue(CreateBall(standable));
                pool_redBalls.Enqueue(CreateBall(NonStandable));
            }
        }
    }

    private GameObject CreateBall(Color ballColor)
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.SetActive(true);
        ball.GetComponent<Renderer>().material = new(baseMaterial);
        ball.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        ball.GetComponent<Renderer>().receiveShadows = false;
        ball.GetComponent<Collider>().enabled = false;
        ball.GetComponent<Renderer>().material.SetColor("_BaseColor", ballColor);
        ball.transform.localScale = Vector3.one / 5;
        ball.transform.position = Vector3.zero + Vector3.down * 5000f;
        return ball;
    }

    private void FixedUpdate()
    {
        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<MainCamera>();
                return;
            }

            if (configMode.Value == Mode.Continuous && !isVisualizationRunning)
            {
                isVisualizationRunning = true;
                StartCoroutine(RenderChangedVisualizationCoroutine());
            }
        }
    }

    private void Update()
    {
        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<MainCamera>();
                return;
            }

            CheckHotkeys();
            if (configMode.Value == Mode.FadeAway) SetBallAlphas();
        }
    }

    // Keeping Update clean by factoring this out
    private void CheckHotkeys()
    {
        if (Input.GetKeyDown(configActivationKey.Value))
        {
            if (isVisualizationRunning) return;
            if (configMode.Value == Mode.Continuous) return;

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
        if (ball.ballObject != null)
        {
            ball.ballObject.SetActive(false);
            totalCalls++;
            if (ball.standable)
            {
                pool_balls.Enqueue(ball.ballObject);
            }
            else
            {
                pool_redBalls.Enqueue(ball.ballObject);
            }
            ball.ballObject = null;
        }
    }


    private void PlaceBall(PositionKey positionKey)
    {
        GameObject ball = null;

        if (positionKey.standable)
        {
            if (pool_balls.Count == 0)
            {
                for (int i = 0; i < poolSize / 10; i++)
                {
                    pool_balls.Enqueue(CreateBall(standable));
                    totalCalls++;
                }
            }
            ball = pool_balls.Dequeue();
        }
        else
        {
            if (pool_redBalls.Count == 0)
            {
                for (int i = 0; i < poolSize / 10; i++)
                {
                    pool_redBalls.Enqueue(CreateBall(NonStandable));
                    totalCalls++;
                }
            }
            ball = pool_redBalls.Dequeue();
        }

        ball.transform.position = positionKey.position;
        positionKey.ballObject = ball;
        ball.SetActive(true);

        totalCalls++;
    }

    private IEnumerator RenderVisualizationCoroutine()
    {
        additionalDebugInfo = "";

        ReturnAllBallsToPool();
        lastScanTime = Time.time;

        int totalCalls = 0;

        Camera theCamera = Camera.main;

        // constrain to grid, and make sure there's no weird floating point junk
        nearestGridToCamera.x = (int)Mathf.Round(Mathf.Round(mainCamera.transform.position.x / freq) * freq * 10f); 
        nearestGridToCamera.y = mainCamera.transform.position.y;
        nearestGridToCamera.z = (int)Mathf.Round(Mathf.Round(mainCamera.transform.position.z / freq) * freq * 10f);
        

        for (int x = -safeRange; x <= safeRange; x += safeFreq)
        {
            for (int z = -safeRange; z <= safeRange; z += safeFreq)
            {
                try
                {
                    CheckAndPlaceBallsInVerticalRay(nearestGridToCamera.x + x, nearestGridToCamera.y, nearestGridToCamera.z + z);
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


    private IEnumerator RenderChangedVisualizationCoroutine()
    {
        additionalDebugInfo = "";

        lastScanTime = Time.time;
        
        totalCalls = 0;
        bool newRay = true;
        Camera theCamera = Camera.main;


        Rect oldArea = Rect.MinMaxRect(nearestGridToCamera.x - safeRange, nearestGridToCamera.z - safeRange, nearestGridToCamera.x + safeRange, nearestGridToCamera.z + safeRange);

        // constrain to grid, and make sure there's no weird floating point junk
        nearestGridToCamera.x = (int)Mathf.Round(Mathf.Round(mainCamera.transform.position.x / freq) * freq * 10f); 
        nearestGridToCamera.y = mainCamera.transform.position.y;
        nearestGridToCamera.z = (int)Mathf.Round(Mathf.Round(mainCamera.transform.position.z / freq) * freq * 10f);
        

        Rect newArea = Rect.MinMaxRect(nearestGridToCamera.x - safeRange, nearestGridToCamera.z - safeRange, nearestGridToCamera.x + safeRange, nearestGridToCamera.z + safeRange);

        var newAreas = SubtractRect(newArea, oldArea);
        var expiredAreas = SubtractRect(oldArea, newArea);


        // Find actual intersection (clamped to outer)
        float xMin = Mathf.Max(newArea.xMin, oldArea.xMin);
        float xMax = Mathf.Min(newArea.xMax, oldArea.xMax);
        float yMin = Mathf.Max(newArea.yMin, oldArea.yMin);
        float yMax = Mathf.Min(newArea.yMax, oldArea.yMax);

        Rect unchangedArea = Rect.zero; 
        
        if (xMax > xMin && yMax > yMin)
        {
            unchangedArea = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }



        foreach (var area in newAreas)
        {
            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    try
                    {
                        newRay = CheckCacheMiss(x, nearestGridToCamera.y, z);
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
                    
                    try
                    {
                        PlaceBallsInVerticalRay(x, nearestGridToCamera.y, z, newRay);
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
        }

        foreach (var area in expiredAreas)
        {
            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    try
                    {
                        RemoveBallsInVerticalRay(x, z);
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
        }

        
        if (xMax > xMin && yMax > yMin)
        {
            float top = nearestGridToCamera.y + range;
            float bottom = nearestGridToCamera.y - range;

            Rect area = unchangedArea;
            PositionYList yDict;


            int axMax = (int)Mathf.Round(area.xMax);
            int ayMax = (int)Mathf.Round(area.yMax);

            for (int x = (int)Mathf.Round(area.xMin); x <= axMax; x += safeFreq)
            {
                for (int z = (int)Mathf.Round(area.yMin); z <= ayMax; z += safeFreq)
                {
                    if (positionCache.TryGetValue((x, z), out yDict))
                    {
                        if (yDict.FirstOutOfRangeAbove > top && yDict.FirstOutOfRangeBelow < bottom)
                        {
                            continue;
                        }
                    
                        try
                        {
                            newRay = CheckCacheMiss(x, nearestGridToCamera.y, z);
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
                        
                        try
                        {
                            PlaceBallsInVerticalRay(x, nearestGridToCamera.y, z, newRay);
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
            }
        }

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

    private void CheckAndPlaceBallsInVerticalRay(int x, float y, int z)
    {
        bool newRay = CheckCacheMiss(x, y, z);

        PlaceBallsInVerticalRay(x, y, z, newRay);
    }

    
    private void PlaceBallsInVerticalRay(int x, float y, int z, bool newRay)
    {
        int xIndex = x;
        int zIndex = z;


        if (positionCache.TryGetValue((xIndex, zIndex), out var yDict))
        {
            yDict.FirstOutOfRangeAbove = -1;
            yDict.FirstOutOfRangeBelow = -1;

            foreach (var kvp in yDict.list)
            {
                PositionKey cachedKey = kvp.Value;

                if (cachedKey != null)
                {
                    float yDist = Mathf.Abs(y - cachedKey.position.y);

                    if (y + range < cachedKey.position.y) 
                    {
                        if (cachedKey.position.y < yDict.FirstOutOfRangeAbove || yDict.FirstOutOfRangeAbove == -1)
                        {
                            yDict.FirstOutOfRangeAbove = cachedKey.position.y;
                        }
                        if (cachedKey.ballObject == null) continue;
                    }
                    else if (y - range > cachedKey.position.y)
                    {
                        if (cachedKey.position.y > yDict.FirstOutOfRangeBelow || yDict.FirstOutOfRangeBelow == -1)
                        {   
                            yDict.FirstOutOfRangeBelow = cachedKey.position.y;
                        }
                        if (cachedKey.ballObject == null) continue;
                    }

                    if (cachedKey.ballObject == null)
                    {
                        PlaceBall(cachedKey);
                    }
                    else if (cachedKey.ballObject != null && yDist > range)
                    {
                        ReturnBallToPool(cachedKey);
                    }
                }
            }
        }
    }

    private void RemoveBallsInVerticalRay(int x, int z)
    {
        int xIndex = x;
        int zIndex = z;

        if (positionCache.TryGetValue((xIndex, zIndex), out var yDict))
        {
            positionCache.Remove((xIndex, zIndex)); // clear cache for this vertical line. Consider keeping it for a certain larger radius?
            yDict.ReturnToPool();
        }
    }

    private bool CheckCacheMiss(int x, float y, int z)
    {
        // cache miss, need to do raycasts to build cache for this vertical line
        if (!positionCache.ContainsKey((x, z)))
        {
            var pCache = PositionYList.GetNew();
            
            // Raycast downwards from the position to check for terrain.
            // Do a full vertical line, and cache all results for this (x,z) position
            // Stop the line just before 0 y, since we don't need to be visualizing the shallow water floor
            // Remember that x/z are increased in magnitude for index purposes
            Vector3 from = new Vector3(x/10f, y, z/10f);
            from.y = 100000f;

            int hitcount = Physics.RaycastNonAlloc(from, Vector3.down, _terrainHitBuffer, 99999f, HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap), QueryTriggerInteraction.UseGlobal);

            totalCalls++;

            for (int i = 0; i < hitcount; i++)
            {
                RaycastHit raycastHit = _terrainHitBuffer[i];

                if (!raycastHit.transform) continue;

                if (raycastHit.point.y < 0.5f) continue; // ignore surfaces below sea level

                CollisionModifier component = raycastHit.collider.GetComponent<CollisionModifier>();
                if (component && !component.standable) continue;

                float angle = Vector3.Angle(Vector3.up, raycastHit.normal);

                if (angle < 30f) continue; // don't need to show on flat ground

                if (pCache.list.ContainsKey((int)Mathf.Round(raycastHit.point.y * 10f))) continue;

                PositionKey positionKey = PositionKey.GetNew(
                    raycastHit.point.x,
                    raycastHit.point.y,
                    raycastHit.point.z,
                    angle < 50f
                );
                
                pCache.list.Add((int)Mathf.Round(raycastHit.point.y * 10f), positionKey);
            }
            
            positionCache[(x, z)] = pCache;

            return true;
        }
        return false;
    }

    // This method is dedicated to VicVoss
    private void SetBallAlphas()
    {
        if (configMode.Value != Mode.FadeAway) return; // this shouldn't be needed but it's good to be safe

        // effectively restrict framerate to 20 for performance
        if (Time.time - lastAlphaChangeTime < 0.05) return;
        lastAlphaChangeTime = Time.time;

        alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01((Time.time - (lastScanTime + 3)) / 3));

        foreach (var pkc in positionCache.Values)
        {
            foreach (var ball in pkc.list.Values)
            {
                if (ball != null)
                {
                    if (ball.ballObject == null) continue;
                    Material mat = ball.ballObject.GetComponent<Renderer>().material;
                    Color baseColor = mat.GetColor("_BaseColor");
                    baseColor.a = alpha;
                    mat.SetColor("_BaseColor", baseColor);
                }
            }
        }

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
        public GameObject ballObject;

        
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
            drop.standable = standable;
            drop.ballObject = ballObject;
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
            drop.standable = standable;
            drop.ballObject = ballObject;
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
            ballObject = null;
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


        private bool isInUse = false;

        public readonly Dictionary<int, PositionKey> list = new();

        public float FirstOutOfRangeAbove = -1;
        public float FirstOutOfRangeBelow = -1;

        
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
            drop.FirstOutOfRangeAbove = -1;
            drop.FirstOutOfRangeBelow = -1;
            drop.isInUse = true;
            totalInUse++;
            return drop;

        }

        public void ReturnToPool()
        {
            if (isInUse == false) return;

            totalInUse--;
            isInUse = false;

            FirstOutOfRangeAbove = -1;
            FirstOutOfRangeBelow = -1;
            
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

    struct SafeXZ
    {
        public int x;
        public float y;
        public int z;
    }

}
