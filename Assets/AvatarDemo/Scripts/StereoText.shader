// TextMesh's default shader is not stereo-instancing aware, so in a single-pass headset it can show in one
// eye only. This one draws the font atlas's alpha in the vertex color, in both eyes.
Shader "AvatarDemo/StereoText"
{
    Properties { _MainTex ("Font Texture", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            struct A { float4 positionOS : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            V vert(A i) { V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                          o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; o.uv = i.uv; return o; }
            half4 frag(V i) : SV_Target { UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                          return half4(i.color.rgb, i.color.a * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a); }
            ENDHLSL
        }
    }
}
