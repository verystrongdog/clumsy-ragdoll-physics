// ★ 2026-10-02：**双面版 Standard**。
// 为什么需要它：owner 报「右胳膊能看到内壁，且随视角变化换地方」——这是**背面剔除**的典型表现
// （近侧壁被判成背面剔掉，看到的是远侧壁的内表面）。Unity 内置的 Standard 材质**不暴露 _Cull**，
// 所以只能拿一个等价的 surface shader 顶上：贴图/颜色/法线/自发光全部照搬，只把 Cull 关掉。
// 背面用 VFACE 把法线翻过来，否则背面会按外表面打光（看着发黑）。
Shader "ClumsyRagdoll/DoubleSidedStandard"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1.0
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _EmissionMap ("Emission", 2D) = "black" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        Cull Off
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        sampler2D _MainTex;
        sampler2D _BumpMap;
        sampler2D _OcclusionMap;
        sampler2D _EmissionMap;
        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
            float2 uv_OcclusionMap;
            float2 uv_EmissionMap;
            float facing : VFACE;
        };
        half _Glossiness;
        half _Metallic;
        half _BumpScale;
        fixed4 _Color;
        fixed4 _EmissionColor;
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Normal = UnpackScaleNormal(tex2D(_BumpMap, IN.uv_BumpMap), _BumpScale) * sign(IN.facing);
            o.Occlusion = tex2D(_OcclusionMap, IN.uv_OcclusionMap).g;
            o.Emission = tex2D(_EmissionMap, IN.uv_EmissionMap).rgb * _EmissionColor.rgb;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Standard"
}