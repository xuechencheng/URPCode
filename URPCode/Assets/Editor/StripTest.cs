using System.Collections;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

public class StripTest : IPreprocessShaders
{
    public int callbackOrder { get { return 1; } }

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        //Debug.LogError("Hello World");
    }
}
