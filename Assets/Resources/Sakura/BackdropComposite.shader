Shader "Hidden/Sakura/BackdropComposite"
{
    Properties { _MainTex("Avatar", 2D) = "black" {} _Background("Background", Color) = (0,1,0,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Background;
            float4 frag(v2f_img input) : SV_Target
            {
                float4 avatar = tex2D(_MainTex, input.uv);
                return float4(avatar.rgb + _Background.rgb * (1 - avatar.a), 1);
            }
            ENDHLSL
        }
    }
}
