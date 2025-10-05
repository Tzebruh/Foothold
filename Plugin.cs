using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Foothold;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static Scene currentScene;
    internal static MainCamera? mainCamera;
    internal static bool activated = false;
    internal static Material baseMaterial;
    internal static float alpha = 1;

    private static readonly List<GameObject> balls = []; // good description of this mod
    private static readonly List<GameObject> redBalls = [];
    private static readonly List<GameObject> pool_balls = [];
    private static readonly List<GameObject> pool_redBalls = [];
    private static float lastScanTime = 0; // should only be needed when fade away is on
    private static float lastAlphaChangeTime = 0; // should only be needed when fade away is on

    private ConfigEntry<KeyCode> configActivationKey;
    private ConfigEntry<bool> configFadeAway;
    private ConfigEntry<bool> configDebugMode;

    private void Awake()
    {
        Logger = base.Logger;

        configActivationKey = Config.Bind("General", "ActivationKey", KeyCode.F);
        configFadeAway = Config.Bind("General", "FadeAway", false, "Replaces the toggle behavior with fading away each scan after 3 seconds, credit to VicVoss on GitHub for the idea");
        configDebugMode = Config.Bind("General", "DebugMode", false, "Show debug information");

        Material mat = new(Shader.Find("Universal Render Pipeline/Lit"));
        // permanently borrowed from https://discussions.unity.com/t/how-to-make-a-urp-lit-material-semi-transparent-using-script-and-then-set-it-back-to-being-solid/942231/3
        mat.SetFloat("_Surface", 1);
        mat.SetFloat("_Blend", 0);
        mat.SetFloat("_ZWrite", 0);
        mat.SetFloat("_ReceiveShadows", 0.0f);
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        baseMaterial = mat;

        SceneManager.sceneLoaded += OnSceneLoaded;

        Logger.LogInfo($"Loaded Foothold? version {MyPluginInfo.PLUGIN_VERSION}");
    }
    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }
    private void OnGUI()
    {
        if (!configDebugMode.Value) return;
        GUILayout.Label("balls: " + balls.Count);
        GUILayout.Label("redBalls: " + redBalls.Count);
        GUILayout.Label("pool_balls: " + pool_balls.Count);
        GUILayout.Label("pool_redBalls: " + pool_redBalls.Count);
        GUILayout.Label("alpha: " + alpha);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        currentScene = scene;
        // Checking for mainCamera in update because it PROBABLY spawns after scene load for networking reasons but this is a complete guess

        if (currentScene.name.StartsWith("Level_"))
        {
            // Reset mainCamera to null so it can be re-found in the new scene
            mainCamera = null;
            // make pools
            for (int i = 0; i < 4000; i++)
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.SetActive(false);
                ball.GetComponent<Renderer>().material = new(baseMaterial);
                ball.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                ball.GetComponent<Renderer>().receiveShadows = false;
                ball.GetComponent<Collider>().enabled = false;
                ball.transform.localScale = Vector3.one / 5;

                if (i < 2000) pool_balls.Add(ball);
                else
                {
                    // Reset state when leaving game scene
                    mainCamera = null;
                    activated = false;
                    balls.Clear();
                    redBalls.Clear();
            
                    ball.GetComponent<Renderer>().material.SetColor("_BaseColor", Color.red);
                    pool_redBalls.Add(ball);
                }
            }
        }
        else
        {
            // clear pools (this isn't destroying the objects but unloading the scene already did that)
            pool_balls.Clear();
            pool_redBalls.Clear();
        }
    }

    private void Update()
    {
        if (currentScene.name.StartsWith("Level_"))
        {
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<MainCamera>();
                return;
            }
            CheckHotkeys();
            if (configFadeAway.Value) SetBallAlphas();
        }
    }

    // Keeping Update clean by factoring this out
    private void CheckHotkeys()
    {
        if (Input.GetKeyDown(configActivationKey.Value))
        {
            if (!configFadeAway.Value) activated = !activated;
            if (activated || configFadeAway.Value) // The activated check is after the change, so this is checking if it has just been toggled on
            {
                RenderVisualization();
            }
            else
            {
                ReturnBallsToPool();
            }
        }
    }

    private void ReturnBallsToPool()
    {
        List<GameObject> ballsCopy = [.. balls]; // Shouldn't modify the list while iterating so we iterate a clone
        foreach (GameObject ball in ballsCopy)
        {
            ball.SetActive(false);
            balls.Remove(ball);
            pool_balls.Add(ball);
        }
        List<GameObject> redBallsCopy = [.. redBalls]; // Shouldn't modify the list while iterating so we iterate a clone
        foreach (GameObject ball in redBallsCopy)
        {
            ball.SetActive(false);
            redBalls.Remove(ball);
            pool_redBalls.Add(ball);
        }
    }

    // Technically CheckAndPlaceBallAt does the actual rendering, but also technically Unity does the rendering,
    // but also technically Vulkan/DX12/DX11 does the rendering, but also technically the GPU does the rendering,
    // all this to say I don't care and I'll name my methods whatever I want
    private void RenderVisualization()
    {
        if (mainCamera == null) return;
    
        ReturnBallsToPool();
        lastScanTime = Time.time;
        float freq = 0.5f;
        float yFreq = 1f;
        for (float x = -10; x <= 10; x += freq)
        {
            for (float y = -10; y <= 10; y += yFreq)
            {
                for (float z = -10; z <= 10; z += freq)
                {
                    Vector3 position = new(
                        mainCamera.transform.position.x + x,
                        mainCamera.transform.position.y + y,
                        mainCamera.transform.position.z + z
                    );
                    CheckAndPlaceBallAt(position);
                }
            }
        }
    }

    // Most of this code was "borrowed" from CharacterMovement.RaycastGroundCheck
    private void CheckAndPlaceBallAt(Vector3 position)
    {
        Vector3 to = position + Vector3.down * 1;
        RaycastHit raycastHit = HelperFunctions.LineCheck(position, to, HelperFunctions.LayerType.TerrainMap, 0f, QueryTriggerInteraction.Ignore);
        if (raycastHit.transform)
        {
            CollisionModifier component = raycastHit.collider.GetComponent<CollisionModifier>();
            if (component)
            {
                if (!component.standable)
                {
                    return;
                }
            }
            float angle = Vector3.Angle(Vector3.up, raycastHit.normal);
            if (angle > 30f) // lower limit on the angle because showing balls on flat ground is pretty pointless
            {
                if (angle < 50f && pool_balls.Count > 0)
                {
                    GameObject ball = pool_balls.First();
                    ball.transform.position = raycastHit.point;
                    balls.Add(ball);
                    pool_balls.Remove(ball);
                    ball.SetActive(true);
                }
                else if (angle >= 50f && pool_redBalls.Count > 0)
                {
                    GameObject ball = pool_redBalls.First();
                    ball.transform.position = raycastHit.point;
                    redBalls.Add(ball);
                    pool_redBalls.Remove(ball);
                    ball.SetActive(true);
                }
            }
        }
    }

    // This method is dedicated to VicVoss
    private void SetBallAlphas()
    {
        if (!configFadeAway.Value) return; // this shouldn't be needed but it's good to be safe

        // effectively restrict framerate to 20 for performance
        if (Time.time - lastAlphaChangeTime < 0.05) return;
        lastAlphaChangeTime = Time.time;

        alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01((Time.time - (lastScanTime + 3)) / 3));

        foreach (GameObject ball in balls.Concat(redBalls))
        {
            Material mat = ball.GetComponent<Renderer>().material;
            Color baseColor = mat.GetColor("_BaseColor");
            baseColor.a = alpha;
            mat.SetColor("_BaseColor", baseColor);
        }

        if (alpha <= 0)
            {
                if (balls.Count > 0)
                {
                    foreach (GameObject ball in balls.ToList()) // ToList used to clone the list because you can't modify what you're enumerating
                    {
                        ball.SetActive(false);
                        balls.Remove(ball);
                        pool_balls.Add(ball);
                    }
                }
                if (redBalls.Count > 0)
                {
                    foreach (GameObject ball in redBalls.ToList()) // ToList used to clone the list because you can't modify what you're enumerating
                    {
                        ball.SetActive(false);
                        redBalls.Remove(ball);
                        pool_redBalls.Add(ball);
                    }
                }
            }
    }
}
