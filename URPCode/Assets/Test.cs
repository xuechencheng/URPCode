using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class Test : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        var asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        Debug.LogError(asset.msaaSampleCount);
    }
}
