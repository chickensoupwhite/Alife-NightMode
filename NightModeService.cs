using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Bismarck.NightMode;

/// <summary>夜间模式配置。</summary>
public class NightModeConfig
{
    [DisplayName("启用夜间模式")]
    [Description("把 Alife 界面切换为深色。改动即时生效，不需要重启")]
    public bool EnableNightMode { get; set; } = false;

    [DisplayName("定时切换")]
    [Description("开启后只在下面的时间段内进入夜间模式，其余时间保持亮色")]
    public bool ScheduleEnabled { get; set; } = false;

    [DisplayName("夜间开始（小时）")]
    [Description("0-23。填 20 与 7 即表示 20:00 到次日 7:00")]
    public int NightStartHour { get; set; } = 20;

    [DisplayName("亮度")]
    [Description("0.8-1.3。1 为默认，调大整体更亮")]
    public double Brightness { get; set; } = 1.0;

    [DisplayName("对比度")]
    [Description("0.8-1.4。1 为默认，调大层次更分明")]
    public double Contrast { get; set; } = 1.0;

    [DisplayName("夜间结束（小时）")]
    [Description("0-23。小于开始小时即视为跨零点")]
    public int NightEndHour { get; set; } = 7;
}

/// <summary>
/// 夜间模式：把 Alife 界面切成深色。
///
/// 复用程序自带的 AntDesign 暗色样式表（<c>_content/AntDesign/css/ant-design-blazor.dark.css</c>，
/// 本来就在包里，只是页面没引它），再补一层极小的布局覆盖。
/// 渲染走框架 4.5.0 起的 globalUI 全局组件，样式在页面内注入，不改动程序文件，不会被更新覆盖。
/// </summary>
[Module("夜间模式",
    "把 Alife 界面切换为深色主题：复用程序自带的 AntDesign 暗色样式并补一层布局覆盖，支持定时切换。",
    defaultCategory: "Bismarck",
    globalUI: typeof(NightModeWidget))]
public class NightModeService(
    XmlFunctionCaller functionCaller,
    Interactor<NightModeService> interactor,
    ILogger<NightModeService> logger) :
    ChatBehaviour,
    IConfigurable<NightModeConfig>
{
    public NightModeConfig Configuration { get; set; } = null!;

    /// <summary>供全局挂件读取当前实例。</summary>
    public static NightModeService? Current { get; private set; }

    // ===== AI 手动入口 =====

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换 Alife 界面的夜间模式（深色主题）。参数 enabled=true 开、false 关")]
    public Task SetNightMode(bool enabled)
    {
        Configuration.EnableNightMode = enabled;
        Configuration.ScheduleEnabled = false;
        NightModeWidget.RequestRefresh();
        logger.LogInformation("夜间模式：手动切换为 {State}", enabled ? "开" : "关");
        return Task.CompletedTask;
    }

    // ===== 生命周期 =====

    protected override Task OnAwake()
    {
        Current = this;
        XmlHandler xmlHandler = new(this) {
            Description = "切换 Alife 界面的夜间模式（深色主题）",
            Explanation = "把界面切成深色或亮色，改动立即生效，不用重启框架。"
        };
        functionCaller.RegisterHandler(xmlHandler, DocumentMode.Implicit, DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        if (Current == this)
            Current = null;
        return Task.CompletedTask;
    }

    // ===== 逻辑 =====

    /// <summary>当前该不该是深色。</summary>
    public bool ShouldBeDark()
    {
        if (Configuration.EnableNightMode)
            return true;

        if (Configuration.ScheduleEnabled == false)
            return false;

        int start = Math.Clamp(Configuration.NightStartHour, 0, 23);
        int end = Math.Clamp(Configuration.NightEndHour, 0, 23);
        if (start == end)
            return false;

        int hour = DateTime.Now.Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }
}

/// <summary>
/// 夜间模式的全局挂件（框架 4.5.0 起 globalUI 全局渲染）。
/// 每秒对一次状态，只在需要切换时往页面里注入/移除样式；页面重载后由本组件自动补回。
/// </summary>
public class NightModeWidget : ComponentBase, IDisposable
{
    [Inject]
    IJSRuntime Js { get; set; } = null!;

    static NightModeWidget? instance;
    Timer? timer;
    bool? applied;
    bool managerReady;
    double lastB = -1;
    double lastC = -1;


    /// <summary>外部（AI 手动切换）请求立刻复核一次。挂件每秒幂等重注，此处留空。</summary>
    public static void RequestRefresh()
    {
        if (instance != null)
            instance.applied = null;
    }

    protected override void OnInitialized()
    {
        instance = this;
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // 等渲染上下文就绪后再起定时器，避免 JSInterop 未就绪时每秒空转刷错
            timer = new Timer(_ => Tick(), null, 2000, 3000);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
        if (instance == this)
            instance = null;
    }

    void Tick()
    {
        try
        {
            NightModeService? service = NightModeService.Current;
            if (service == null)
                return;

            bool dark = service.ShouldBeDark();

            double b = 1, c = 1;
            try
            {
                b = service.Configuration.Brightness;
                c = service.Configuration.Contrast;
            }
            catch (Exception)
            {
            }
            if (applied == dark && Math.Abs(b - lastB) < 0.001 && Math.Abs(c - lastC) < 0.001)
                return;

            applied = dark;
            lastB = b;
            lastC = c;
            _ = InvokeAsync(() => ApplyAsync(dark, b, c));
        }
        catch (Exception)
        {
            // 挂件出错不影响框架，下一轮再试
        }
    }

    async Task ApplyAsync(bool dark, double brightness, double contrast)
    {
        try
        {
            if (!managerReady)
            {
                string r = await Js.InvokeAsync<string>("eval", BuildManager());
                managerReady = (r == "ok" || r == "ready");
            }
            string t = await Js.InvokeAsync<string>("eval", BuildTick(dark, brightness, contrast));
            if (t == "nomanager")
                managerReady = false;
        }
        catch (Exception)
        {
            applied = null;
            managerReady = false;
        }
    }

    /// <summary>页面脚本：一次建好管理器 __bnMgr。</summary>
    static string BuildManager()
    {
        return "(function(){"
            + "var VER='bn31';"
            + "if(window.__bnMgr&&window.__bnMgr.ver===VER)return 'ok';"
            + "if(window.__bnMgr){try{window.__bnMgr.stop();}catch(e){}try{window.__bnMgr.off2();}catch(e){}window.__bnMgr=null;}"
            + "var CSS=atob('" + OverrideCssBase64 + "');"
            + "var BASE={base:'#1e1e2e',main:'#292c3c',card:'#313244',card2:'#45475a',side:'#181825',line:'#585b70',fg:'#cdd6f4',fg2:'#a6adc8',accent:'#89b4fa'};"
            + "var M={on:false,b:1,c:1,busy:false,obs:null,force:null,fixing:false,lastSweep:0,scanT:0};"
            + "M.shade=function(hex,off){"
            + "var r=parseInt(hex.substr(1,2),16),g=parseInt(hex.substr(3,2),16),b=parseInt(hex.substr(5,2),16);"
            + "var d=Math.round(255*off);"
            + "r=Math.max(0,Math.min(255,r+d));g=Math.max(0,Math.min(255,g+d));b=Math.max(0,Math.min(255,b+d));"
            + "return '#'+((1<<24)+(r<<16)+(g<<8)+b).toString(16).slice(1);"
            + "};"
            + "M.tune=function(){"
            + "var t=document.getElementById('bismarck-night-tune');"
            + "if(!t){t=document.createElement('style');t.id='bismarck-night-tune';document.head.appendChild(t);}"
            + "var lum=(M.b-1)*0.30, ctr=(M.c-1)*0.25;"
            + "var dOff=lum-ctr, lOff=lum+ctr;"
            + "t.textContent=':root{'"
            + "+'--bn-base:'+M.shade(BASE.base,dOff)+';'"
            + "+'--bn-main:'+M.shade(BASE.main,dOff)+';'"
            + "+'--bn-card:'+M.shade(BASE.card,dOff)+';'"
            + "+'--bn-card2:'+M.shade(BASE.card2,dOff)+';'"
            + "+'--bn-side:'+M.shade(BASE.side,dOff)+';'"
            + "+'--bn-line:'+M.shade(BASE.line,lum)+';'"
            + "+'--bn-fg:'+M.shade(BASE.fg,lOff)+';'"
            + "+'--bn-fg2:'+M.shade(BASE.fg2,lOff)+';'"
            + "+'--bn-accent:'+M.shade(BASE.accent,lOff)+';}';"
            + "};"
            + "M.setParams=function(b,c){"
            + "b=parseFloat(b)||1;c=parseFloat(c)||1;"
            + "if(b===M.b&&c===M.c)return;"
            + "M.b=b;M.c=c;"
            + "if(M.on)M.tune();"
            + "};"
            + "M.stop=function(){if(M.obs){try{M.obs.disconnect();}catch(e){}M.obs=null;}};"
            + "M.on2=function(){"
            + "var l=document.getElementById('bismarck-night-dark');"
            + "if(!l){l=document.createElement('link');l.id='bismarck-night-dark';l.rel='stylesheet';l.href=location.origin+'/_content/AntDesign/css/ant-design-blazor.dark.css';document.head.appendChild(l);}"
            + "var s=document.getElementById('bismarck-night-patch');"
            + "if(!s){s=document.createElement('style');s.id='bismarck-night-patch';s.textContent=CSS;document.head.appendChild(s);}"
            + "M.tune();"
            + "document.documentElement.classList.add('bn-dark');"
            + "};"
            + "M.purge=function(){"
            + "var PAL=['#303446','#292c3c','#414559','#3b3e54','#5b6078','#c6d0f5','#b7bfdc','#8ab4f8'];"
            + "var all=document.querySelectorAll('body *');"
            + "for(var i=0;i<all.length;i++){var e=all[i];"
            + "if(e.getAttribute&&e.getAttribute('data-bn-fix'))e.removeAttribute('data-bn-fix');"
            + "var ov=e.classList&&(e.classList.contains('bn-ov')||e.classList.contains('bn-rs'));"
            + "if(e.classList){['bn-bg','bn-fg','bn-tr','bn-bf','bn-ov','bn-rs'].forEach(function(c){e.classList.remove(c);});}"
            + "var s=e.style;if(!s||!s.cssText)continue;"
            + "['background-color','background','color','border-color','border'].forEach(function(p){"
            + "var v=s.getPropertyValue(p);"
            + "if(v&&(v.indexOf('--bn-')>=0||PAL.indexOf(v)>=0))s.removeProperty(p);});"
            + "if(s.getPropertyValue('backdrop-filter'))s.removeProperty('backdrop-filter');"
            + "if(s.getPropertyValue('-webkit-backdrop-filter'))s.removeProperty('-webkit-backdrop-filter');"
            + "if(ov){s.removeProperty('background');s.removeProperty('background-color');}}"
            + "};"
            + "M.off2=function(){try{M.purge();}catch(e){}"
            + "document.documentElement.classList.remove('bn-dark');"
            + "['bismarck-night-dark','bismarck-night-patch','bismarck-night-tune'].forEach(function(id){"
            + "var e=document.getElementById(id);if(e&&e.parentNode)e.parentNode.removeChild(e);});"
            + "M.purge();"
            + "};"
            + "M.fix=function(e){"
            + "if(!M.on||!e||e.nodeType!==1)return;"
            + "var tag=e.tagName;"
            + "if(tag==='IMG'||tag==='CANVAS'||tag==='VIDEO'||tag==='SVG'||tag==='PATH'||tag==='USE')return;"
            + "if(e.id&&e.id.indexOf('bismarck-night')===0)return;"
            + "var cs=getComputedStyle(e);"
            + "var bf=cs.backdropFilter||cs.webkitBackdropFilter||'';"
            + "if(bf&&bf!=='none'){e.classList.add('bn-bf');e.style.setProperty('backdrop-filter','none','important');e.style.setProperty('-webkit-backdrop-filter','none','important');}"
            + "if(e.classList.contains('overlay-layer')){e.classList.add('bn-ov');e.style.setProperty('background','transparent','important');}"
            + "if(e.classList.contains('chat-reasoning-reason')){e.classList.add('bn-rs');e.style.setProperty('background-color','var(--bn-card)','important');}"
            + "var bg=cs.backgroundColor,n1=bg.match(/[0-9.]+/g);"
            + "if(n1&&n1.length>=3){"
            + "var a1=n1.length>=4?parseFloat(n1[3]):1;"
            + "var l1=(0.2126*parseFloat(n1[0])+0.7152*parseFloat(n1[1])+0.0722*parseFloat(n1[2]))/255;"
            + "if(e.closest&&e.closest('.ant-btn,.ant-tag,.ant-switch,.ant-checkbox,.ant-radio,.ant-badge,.anticon,.ant-avatar'))return;"
            + "var rc=e.getBoundingClientRect(),ar=rc.width*rc.height;"
            + "if(a1>0.02&&l1>0.55&&ar>300){e.classList.add('bn-bg');return;}"
            + "if(a1>0.02&&a1<0.9&&l1<=0.55){e.classList.add('bn-tr');}}"
            + "var bt=parseFloat(cs.borderTopWidth)||0,bl=parseFloat(cs.borderLeftWidth)||0;"
            + "if(bt>0||bl>0){var bc=cs.borderTopColor,bm=bc.match(/[0-9.]+/g);"
            + "if(bm&&bm.length>=3){var ba=bm.length>=4?parseFloat(bm[3]):1;"
            + "var bL=(0.2126*parseFloat(bm[0])+0.7152*parseFloat(bm[1])+0.0722*parseFloat(bm[2]))/255;"
            + "if(ba>0.02&&bL>0.45)e.classList.add('bn-bd');}}"
            + "var fg=cs.color,n2=fg.match(/[0-9.]+/g);"
            + "if(n2&&n2.length>=3){"
            + "var a2=n2.length>=4?parseFloat(n2[3]):1;"
            + "var l2=(0.2126*parseFloat(n2[0])+0.7152*parseFloat(n2[1])+0.0722*parseFloat(n2[2]))/255;"
            + "if(a2>=0.9&&l2<0.55){e.classList.add('bn-fg');}}"
            + "};"
            + "M.sweep=function(force){"
            + "var now=Date.now();"
            + "if(!force&&now-(M.lastSweep||0)<15000)return;"
            + "M.lastSweep=now;"
            + "var o=M.obs;M.fixing=true;"
            + "if(o)try{o.disconnect();}catch(e){}"
            + "M.fix(document.body);"
            + "var a=document.querySelectorAll('body *');"
            + "for(var i=0;i<a.length;i++)M.fix(a[i]);"
            + "if(o)try{o.observe(document.body,{childList:true,subtree:true});}catch(e){}"
            + "M.fixing=false;"
            + "};"
            + "M.set=function(on,manual){if(manual)M.force=on;"
            + "if(M.busy)return;"
            + "if(on===M.on&&(on===false||document.getElementById('bismarck-night-patch')))return;"
            + "M.busy=true;"
            + "try{"
            + "if(on){M.on=true;M.on2();M.watch();M.sweep(true);}"
            + "else{M.on=false;M.stop();M.off2();}"
            + "var b=document.getElementById('bismarck-night-btn');"
            + "if(b)b.textContent=on?'\\u591c':'\\u65e5';"
            + "}catch(e){}finally{M.busy=false;}"
            + "};"
            + "M.watch=function(){"
            + "if(M.obs||!M.on)return;"

            + "M.obs=new MutationObserver(function(ms){"
            + "if(!M.on||M.fixing)return;"
            + "for(var i=0;i<ms.length;i++){var m=ms[i];"
            + "for(var j=0;j<m.addedNodes.length;j++){var nd=m.addedNodes[j];M.fix(nd);"
            + "if(nd.querySelectorAll){var a=nd.querySelectorAll('*');for(var k=0;k<a.length;k++)M.fix(a[k]);}}}"
            + "if(M.scanT)clearTimeout(M.scanT);"
            + "M.scanT=setTimeout(function(){M.scanT=0;if(M.on)M.sweep(true);},400);"
            + "});"
            + "M.obs.observe(document.body,{childList:true,subtree:true});"
            + "};"
            + "M.ensureBtn=function(){"
            + "if(document.getElementById('bismarck-night-btn-host'))return;"
            + "var host=document.createElement('div');host.id='bismarck-night-btn-host';"
            + "host.style.cssText='position:fixed;right:18px;bottom:120px;z-index:2147483000;';"
            + "var sh=host.attachShadow({mode:'open'});"
            + "var st=document.createElement('style');"
            + "st.textContent=':host{all:initial}.b{width:38px;height:38px;border-radius:50%;cursor:pointer;border:1px solid #51576d;background:#414559;color:#c6d0f5;font-size:14px;line-height:36px;text-align:center;box-shadow:0 2px 10px rgba(0,0,0,.4);user-select:none;font-family:system-ui,sans-serif}.b:hover{background:#51576d}';"
            + "sh.appendChild(st);"
            + "var el=document.createElement('div');el.className='b';el.id='bismarck-night-btn';el.textContent=M.on?'\\u591c':'\\u65e5';"
            + "sh.appendChild(el);"
            + "var drag=false,mx=0,my=0,rx=0,ry=0;"
            + "el.addEventListener('mousedown',function(ev){drag=false;mx=ev.clientX;my=ev.clientY;var r=host.getBoundingClientRect();rx=r.right;ry=r.bottom;ev.preventDefault();});"
            + "window.addEventListener('mousemove',function(ev){"
            + "if(rx===0)return;var dx=ev.clientX-mx,dy=ev.clientY-my;"
            + "if(Math.abs(dx)+Math.abs(dy)>3)drag=true;"
            + "host.style.right=Math.max(4,window.innerWidth-rx-dx)+'px';"
            + "host.style.bottom=Math.max(4,window.innerHeight-ry-dy)+'px';});"
            + "window.addEventListener('mouseup',function(ev){"
            + "if(rx===0)return;var d=drag;rx=0;ry=0;"
            + "if(!d){M.set(!M.on,true);"
            + "var b=document.getElementById('bismarck-night-btn');"
            + "if(b)b.textContent=M.on?'\\u591c':'\\u65e5';}});"
            + "document.body.appendChild(host);"
            + "};"
            + "M.ver=VER;window.__bnMgr=M;M.off2();"
            + "return 'ready';"
            + "})()";
    }

    /// <summary>每轮同步状态与色阶参数。</summary>
    static string BuildTick(bool dark, double brightness, double contrast)
    {
        string b = brightness.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string c = contrast.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "(function(){"
            + "var M=window.__bnMgr;if(!M)return 'nomanager';"
            + "M.ensureBtn();M.setParams(" + b + "," + c + ");"
            + "M.set(" + (dark ? "true" : "false") + ",false);"
            + "if(M.on)M.sweep();"
            + "return (M.on?'on':'off');"
            + "})()";
    }

    static string BuildScript(bool dark)
    {
        return BuildTick(dark, 1, 1);
    }

    const string OverrideCssBase64 = "LyogQmlzbWFyY2suTmlnaHRNb2RlIMK3IENhdHBwdWNjaW4gTW9jaGEg5YiG5bGCICovCmh0bWwuYm4tZGFyayxodG1sLmJuLWRhcmsgYm9keSxodG1sLmJuLWRhcmsgLmFwcC1jb250YWluZXJ7YmFja2dyb3VuZDojMWUxZTJlICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAubWFpbi1jb250ZW50LGh0bWwuYm4tZGFyayAubWFpbi1jb250ZW50PmRpdixodG1sLmJuLWRhcmsgLm1haW4tY29udGVudD5kaXY+ZGl2e2JhY2tncm91bmQ6IzFlMWUyZSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgI21lc3NhZ2VBcmVhLGh0bWwuYm4tZGFyayAjbWVzc2FnZUFyZWE+ZGl2LGh0bWwuYm4tZGFyayAjbWVzc2FnZUFyZWE+ZGl2PmRpdntiYWNrZ3JvdW5kOiMyOTJjM2MgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5nbG9iYWwtc2lkZWJhcixodG1sLmJuLWRhcmsgLmFudC1sYXlvdXQtc2lkZXJ7YmFja2dyb3VuZDojMTgxODI1ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuY2hhdC1pbnB1dC1jb250YWluZXJ7YmFja2dyb3VuZDojMTgxODI1ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuY2hhdC1pbnB1dC1jb250YWluZXIgLmFudC1pbnB1dCxodG1sLmJuLWRhcmsgLmNoYXQtaW5wdXQtY29udGFpbmVyIHRleHRhcmVhe2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmNoYXQtYnViYmxlLXVzZXJ7YmFja2dyb3VuZDojNDU0NzVhICFpbXBvcnRhbnQ7Ym9yZGVyOjAgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5jaGF0LWJ1YmJsZS1haXtiYWNrZ3JvdW5kOiMzMTMyNDQgIWltcG9ydGFudDtib3JkZXI6MCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmNoYXQtcmVhc29uaW5nLXJlYXNvbntiYWNrZ3JvdW5kOiMxZTFlMmUgIWltcG9ydGFudDtib3JkZXI6MCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLm92ZXJsYXktbGF5ZXIsaHRtbC5ibi1kYXJrIC5hbnQtZHJhd2VyLW1hc2ssaHRtbC5ibi1kYXJrIC5hbnQtbW9kYWwtbWFza3tiYWNrZ3JvdW5kOnJnYmEoMTcsMTcsMjcsLjcyKSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1kcmF3ZXItY29udGVudCxodG1sLmJuLWRhcmsgLmFudC1kcmF3ZXItYm9keSxodG1sLmJuLWRhcmsgLmFudC1kcmF3ZXItaGVhZGVyLGh0bWwuYm4tZGFyayAuYW50LWRyYXdlci10aXRsZSxodG1sLmJuLWRhcmsgLmFudC1kcmF3ZXItd3JhcHBlci1ib2R5e2JhY2tncm91bmQ6IzFlMWUyZSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1tb2RhbC1jb250ZW50LGh0bWwuYm4tZGFyayAuYW50LW1vZGFsLWhlYWRlcixodG1sLmJuLWRhcmsgLmFudC1tb2RhbC10aXRsZSxodG1sLmJuLWRhcmsgLmFudC1tb2RhbC1ib2R5LGh0bWwuYm4tZGFyayAuYW50LW1vZGFsLWZvb3RlcntiYWNrZ3JvdW5kOiMxZTFlMmUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtbGF5b3V0LGh0bWwuYm4tZGFyayAuYW50LWxheW91dC1jb250ZW50LGh0bWwuYm4tZGFyayAuYW50LWxheW91dC1oZWFkZXIsaHRtbC5ibi1kYXJrIC5hbnQtbGF5b3V0LWZvb3RlcntiYWNrZ3JvdW5kOiMxZTFlMmUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtdGFicy1uYXYsaHRtbC5ibi1kYXJrIC5hbnQtdGFicy1jb250ZW50LWhvbGRlcixodG1sLmJuLWRhcmsgLmFudC10YWJzLXRhYnBhbmV7YmFja2dyb3VuZDp0cmFuc3BhcmVudCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC10YWJzLXRhYi1idG57Y29sb3I6I2E2YWRjOCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC10YWJzLXRhYi1hY3RpdmUgLmFudC10YWJzLXRhYi1idG57Y29sb3I6Izg5YjRmYSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC10YWJzLWluay1iYXJ7YmFja2dyb3VuZDojODliNGZhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LWNhcmQsaHRtbC5ibi1kYXJrIC5hbnQtY2FyZC1oZWFkLGh0bWwuYm4tZGFyayAuYW50LWNhcmQtYm9keXtiYWNrZ3JvdW5kOiMzMTMyNDQgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtY29sbGFwc2UsaHRtbC5ibi1kYXJrIC5hbnQtY29sbGFwc2UtaXRlbSxodG1sLmJuLWRhcmsgLmFudC1jb2xsYXBzZS1oZWFkZXIsaHRtbC5ibi1kYXJrIC5hbnQtY29sbGFwc2UtY29udGVudHtiYWNrZ3JvdW5kOiMzMTMyNDQgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtbGlzdCxodG1sLmJuLWRhcmsgLmFudC1saXN0LWl0ZW17YmFja2dyb3VuZDojMzEzMjQ0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LWRlc2NyaXB0aW9ucyxodG1sLmJuLWRhcmsgLmFudC1kZXNjcmlwdGlvbnMtdmlldyxodG1sLmJuLWRhcmsgLmFudC1kZXNjcmlwdGlvbnMtaXRlbS1sYWJlbCxodG1sLmJuLWRhcmsgLmFudC1kZXNjcmlwdGlvbnMtaXRlbS1jb250ZW50e2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1kZXNjcmlwdGlvbnMtaXRlbS1sYWJlbHtjb2xvcjojYTZhZGM4ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXRhYmxle2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC10YWJsZS10aGVhZD50cj50aHtiYWNrZ3JvdW5kOiM0NTQ3NWEgIWltcG9ydGFudDtjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnQ7Ym9yZGVyLWNvbG9yOiM1ODViNzAgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtdGFibGUtdGJvZHk+dHI+dGR7YmFja2dyb3VuZDojMjkyYzNjICFpbXBvcnRhbnQ7Y29sb3I6I2NkZDZmNCAhaW1wb3J0YW50O2JvcmRlci1jb2xvcjojNDU0NzVhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXRhYmxlLXRib2R5PnRyOmhvdmVyPnRke2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1pbnB1dCxodG1sLmJuLWRhcmsgLmFudC1pbnB1dC1hZmZpeC13cmFwcGVyLGh0bWwuYm4tZGFyayB0ZXh0YXJlYSxodG1sLmJuLWRhcmsgaW5wdXR7YmFja2dyb3VuZDojMzEzMjQ0ICFpbXBvcnRhbnQ7Ym9yZGVyLWNvbG9yOiM1ODViNzAgIWltcG9ydGFudDtjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LWlucHV0OjpwbGFjZWhvbGRlcixodG1sLmJuLWRhcmsgdGV4dGFyZWE6OnBsYWNlaG9sZGVye2NvbG9yOiM2YzcwODYgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtYnRue2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50O2JvcmRlci1jb2xvcjojNTg1YjcwICFpbXBvcnRhbnQ7Y29sb3I6I2NkZDZmNCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1idG4tcHJpbWFyeXtiYWNrZ3JvdW5kOiM4OWI0ZmEgIWltcG9ydGFudDtib3JkZXItY29sb3I6Izg5YjRmYSAhaW1wb3J0YW50O2NvbG9yOiMxZTFlMmUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtYnRuLXByaW1hcnkgKntjb2xvcjojMWUxZTJlICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXNlbGVjdC1zZWxlY3RvcixodG1sLmJuLWRhcmsgLmFudC1waWNrZXIsaHRtbC5ibi1kYXJrIC5hbnQtaW5wdXQtbnVtYmVye2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50O2JvcmRlci1jb2xvcjojNTg1YjcwICFpbXBvcnRhbnQ7Y29sb3I6I2NkZDZmNCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1zZWxlY3QtYXJyb3d7Y29sb3I6I2E2YWRjOCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1zZWxlY3QtZHJvcGRvd24saHRtbC5ibi1kYXJrIC5hbnQtZHJvcGRvd24tbWVudSxodG1sLmJuLWRhcmsgLmFudC1waWNrZXItcGFuZWx7YmFja2dyb3VuZDojMzEzMjQ0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXNlbGVjdC1pdGVte2NvbG9yOiNjZGQ2ZjQgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtc2VsZWN0LWl0ZW0tb3B0aW9uLWFjdGl2ZSxodG1sLmJuLWRhcmsgLmFudC1kcm9wZG93bi1tZW51LWl0ZW06aG92ZXJ7YmFja2dyb3VuZDojNDU0NzVhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXN3aXRjaHtiYWNrZ3JvdW5kOiM1ODViNzAgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtc3dpdGNoLWNoZWNrZWR7YmFja2dyb3VuZDojODliNGZhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXRhZ3tiYWNrZ3JvdW5kOiM0NTQ3NWEgIWltcG9ydGFudDtib3JkZXItY29sb3I6IzU4NWI3MCAhaW1wb3J0YW50O2NvbG9yOiNjZGQ2ZjQgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtYWxlcnR7YmFja2dyb3VuZDojMzEzMjQ0ICFpbXBvcnRhbnQ7Ym9yZGVyLWNvbG9yOiM1ODViNzAgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtZW1wdHktZGVzY3JpcHRpb24saHRtbC5ibi1kYXJrIC5hbnQtYmFkZ2Utc3RhdHVzLXRleHQsaHRtbC5ibi1kYXJrIC5hbnQtZm9ybS1pdGVtLWxhYmVsPmxhYmVse2NvbG9yOiNhNmFkYzggIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtZGl2aWRlcixodG1sLmJuLWRhcmsgLmFudC1kaXZpZGVyLXZlcnRpY2Fse2JhY2tncm91bmQ6IzU4NWI3MCAhaW1wb3J0YW50O2JvcmRlci1jb2xvcjojNTg1YjcwICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXJhZGlvLWlubmVyLGh0bWwuYm4tZGFyayAuYW50LWNoZWNrYm94LWlubmVye2JhY2tncm91bmQ6IzMxMzI0NCAhaW1wb3J0YW50O2JvcmRlci1jb2xvcjojNTg1YjcwICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXRvb2x0aXAtaW5uZXIsaHRtbC5ibi1kYXJrIC5hbnQtcG9wb3Zlci1pbm5lcntiYWNrZ3JvdW5kOiMzMTMyNDQgIWltcG9ydGFudDtjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXBhZ2luYXRpb24taXRlbXtiYWNrZ3JvdW5kOiMzMTMyNDQgIWltcG9ydGFudDtib3JkZXItY29sb3I6IzU4NWI3MCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1wYWdpbmF0aW9uLWl0ZW0gYXtjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LXNsaWRlci1yYWlse2JhY2tncm91bmQ6IzU4NWI3MCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC10eXBvZ3JhcGh5e2NvbG9yOiNjZGQ2ZjQgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIGEsaHRtbC5ibi1kYXJrIGEgKntjb2xvcjojODliNGZhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayBib2R5LGh0bWwuYm4tZGFyayAuYXBwLWNvbnRhaW5lcixodG1sLmJuLWRhcmsgLmFwcC1jb250YWluZXIgKntjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuZ2xvYmFsLXNpZGViYXIsaHRtbC5ibi1kYXJrIC5nbG9iYWwtc2lkZWJhciAqe2NvbG9yOiNhNmFkYzggIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrICo6Oi13ZWJraXQtc2Nyb2xsYmFye2JhY2tncm91bmQ6IzE4MTgyNX0KaHRtbC5ibi1kYXJrICo6Oi13ZWJraXQtc2Nyb2xsYmFyLXRodW1ie2JhY2tncm91bmQ6IzU4NWI3MH0KaHRtbC5ibi1kYXJrIC5hcHAtY29udGFpbmVyLGh0bWwuYm4tZGFyayAub3ZlcmxheS1sYXllcntiYWNrZHJvcC1maWx0ZXI6bm9uZSAhaW1wb3J0YW50Oy13ZWJraXQtYmFja2Ryb3AtZmlsdGVyOm5vbmUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5ibi1mZ3tjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYm4tdHJ7YmFja2dyb3VuZC1jb2xvcjp0cmFuc3BhcmVudCAhaW1wb3J0YW50fQoKaHRtbC5ibi1kYXJrIC5ibi1mZ3tjb2xvcjojY2RkNmY0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYm4tdHJ7YmFja2dyb3VuZC1jb2xvcjp0cmFuc3BhcmVudCAhaW1wb3J0YW50fQoKLyogPT09PT0g5a+55q+U5bqm5pS25Y+j77ya5LiJ5qGj5YiG5piOICsg55m957q/5Y6L5Yi2ID09PT09ICovCmh0bWwuYm4tZGFya3tiYWNrZ3JvdW5kOiMxODE4MjUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIGJvZHksaHRtbC5ibi1kYXJrIC5hcHAtY29udGFpbmVye2JhY2tncm91bmQ6IzE4MTgyNSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLm1haW4tY29udGVudCxodG1sLmJuLWRhcmsgLm1haW4tY29udGVudD5kaXYsaHRtbC5ibi1kYXJrIC5tYWluLWNvbnRlbnQ+ZGl2PmRpdntiYWNrZ3JvdW5kOiMxZTFlMmUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrICNtZXNzYWdlQXJlYSxodG1sLmJuLWRhcmsgI21lc3NhZ2VBcmVhPmRpdixodG1sLmJuLWRhcmsgI21lc3NhZ2VBcmVhPmRpdj5kaXZ7YmFja2dyb3VuZDojMWUxZTJlICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuZ2xvYmFsLXNpZGViYXIsaHRtbC5ibi1kYXJrIC5hbnQtbGF5b3V0LXNpZGVye2JhY2tncm91bmQ6IzExMTExYiAhaW1wb3J0YW50O2JvcmRlci1yaWdodC1jb2xvcjojNDU0NzVhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LWxheW91dCxodG1sLmJuLWRhcmsgLmFudC1sYXlvdXQtY29udGVudCxodG1sLmJuLWRhcmsgLmFudC1sYXlvdXQtaGVhZGVyLGh0bWwuYm4tZGFyayAuYW50LWxheW91dC1mb290ZXJ7YmFja2dyb3VuZDojMTgxODI1ICFpbXBvcnRhbnQ7Ym9yZGVyLWNvbG9yOiM0NTQ3NWEgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5jaGF0LWlucHV0LWNvbnRhaW5lcntiYWNrZ3JvdW5kOiMxODE4MjUgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5jaGF0LWJ1YmJsZS11c2VyLGh0bWwuYm4tZGFyayAuY2hhdC1idWJibGUtYWksaHRtbC5ibi1kYXJrIC5jaGF0LXJlYXNvbmluZy1yZWFzb257Ym9yZGVyOjAgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtdGFicy1uYXY6OmJlZm9yZSxodG1sLmJuLWRhcmsgLmFudC10YWJzLW5hdjo6YWZ0ZXJ7YmFja2dyb3VuZDojNDU0NzVhICFpbXBvcnRhbnQ7Ym9yZGVyLWNvbG9yOiM0NTQ3NWEgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtdGFicy10b3A+LmFudC10YWJzLW5hdjo6YmVmb3Jle2JvcmRlci1ib3R0b20tY29sb3I6IzQ1NDc1YSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1jYXJke2JvcmRlci1jb2xvcjojNDU0NzVhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LWNhcmQtaGVhZHtib3JkZXItYm90dG9tLWNvbG9yOiM0NTQ3NWEgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5hbnQtZGl2aWRlcixodG1sLmJuLWRhcmsgLmFudC1kaXZpZGVyLXZlcnRpY2Fse2JhY2tncm91bmQ6IzQ1NDc1YSAhaW1wb3J0YW50O2JvcmRlci1jb2xvcjojNDU0NzVhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayBocntib3JkZXItY29sb3I6IzQ1NDc1YSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC1tb2RhbC1oZWFkZXIsaHRtbC5ibi1kYXJrIC5hbnQtZHJhd2VyLWhlYWRlcntib3JkZXItYm90dG9tLWNvbG9yOiM0NTQ3NWEgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrIC5ibi1iZHtib3JkZXItY29sb3I6IzQ1NDc1YSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmJuLWJkICp7Ym9yZGVyLWNvbG9yOiM0NTQ3NWEgIWltcG9ydGFudH0KCi8qID09PT09IOWxguasoeaUtuWPo++8muS4reWMuuaPkOS6riArIOawlOazoee7hui+ue+8iOS4jeimgeaWueahhu+8iSA9PT09PSAqLwpodG1sLmJuLWRhcmsgLm1haW4tY29udGVudCxodG1sLmJuLWRhcmsgLm1haW4tY29udGVudD5kaXYsaHRtbC5ibi1kYXJrIC5tYWluLWNvbnRlbnQ+ZGl2PmRpdntiYWNrZ3JvdW5kOiMyNDI3M2EgIWltcG9ydGFudH0KaHRtbC5ibi1kYXJrICNtZXNzYWdlQXJlYSxodG1sLmJuLWRhcmsgI21lc3NhZ2VBcmVhPmRpdixodG1sLmJuLWRhcmsgI21lc3NhZ2VBcmVhPmRpdj5kaXZ7YmFja2dyb3VuZDojMjQyNzNhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuY2hhdC1pbnB1dC1jb250YWluZXJ7YmFja2dyb3VuZDojMWUxZTJlICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuZ2xvYmFsLXNpZGViYXIsaHRtbC5ibi1kYXJrIC5hbnQtbGF5b3V0LXNpZGVye2JhY2tncm91bmQ6IzE4MTgyNSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmNoYXQtYnViYmxlLXVzZXJ7YmFja2dyb3VuZDojNDU0NzVhICFpbXBvcnRhbnQ7Ym9yZGVyOjFweCBzb2xpZCAjNTg1YjcwICFpbXBvcnRhbnQ7Ym94LXNoYWRvdzpub25lICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuY2hhdC1idWJibGUtYWl7YmFja2dyb3VuZDojMzEzMjQ0ICFpbXBvcnRhbnQ7Ym9yZGVyOjFweCBzb2xpZCAjNDU0NzVhICFpbXBvcnRhbnQ7Ym94LXNoYWRvdzpub25lICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuY2hhdC1yZWFzb25pbmctcmVhc29ue2JhY2tncm91bmQ6IzFlMWUyZSAhaW1wb3J0YW50O2JvcmRlcjoxcHggc29saWQgIzQ1NDc1YSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLm1haW4tY29udGVudCxodG1sLmJuLWRhcmsgI21lc3NhZ2VBcmVhe2JhY2tncm91bmQ6IzI0MjczYSAhaW1wb3J0YW50fQoKLyogPT09PT0gLmJuLWJnIOaMieWMuuWfn+WIhuaho++8iOWOn+adpeS4gOWIgOWIh+aKueW5s+S6huaJgOacieWxguasoe+8iSA9PT09PSAqLwpodG1sLmJuLWRhcmsgLmJuLWJne2JhY2tncm91bmQtY29sb3I6IzMxMzI0NCAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLm1haW4tY29udGVudCAuYm4tYmcsaHRtbC5ibi1kYXJrICNtZXNzYWdlQXJlYSAuYm4tYmd7YmFja2dyb3VuZC1jb2xvcjojMjQyNzNhICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuZ2xvYmFsLXNpZGViYXIgLmJuLWJnLGh0bWwuYm4tZGFyayAuYW50LWxheW91dC1zaWRlciAuYm4tYmd7YmFja2dyb3VuZC1jb2xvcjojMTgxODI1ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LWNhcmQuYm4tYmcsaHRtbC5ibi1kYXJrIC5hbnQtY2FyZCAuYm4tYmd7YmFja2dyb3VuZC1jb2xvcjojMzEzMjQ0ICFpbXBvcnRhbnR9Cmh0bWwuYm4tZGFyayAuYW50LW1vZGFsIC5ibi1iZyxodG1sLmJuLWRhcmsgLmFudC1kcmF3ZXIgLmJuLWJne2JhY2tncm91bmQtY29sb3I6IzI5MmMzYyAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmNoYXQtaW5wdXQtY29udGFpbmVyIC5ibi1iZyxodG1sLmJuLWRhcmsgLmNoYXQtaW5wdXQtY29udGFpbmVye2JhY2tncm91bmQtY29sb3I6IzFlMWUyZSAhaW1wb3J0YW50fQpodG1sLmJuLWRhcmsgLmFudC10YWJsZS10Ym9keSAuYm4tYmcsaHRtbC5ibi1kYXJrIC5hbnQtdGFibGUtdGhlYWQgLmJuLWJne2JhY2tncm91bmQtY29sb3I6IzI5MmMzYyAhaW1wb3J0YW50fQo=";
}
