using AEAssist.AEPlugin;
using AEAssist.Verify;
using System.Runtime.Loader;
using WhiteAxisRecorder.Recorder;

namespace WhiteAxisRecorder.Plugin
{
    public sealed class WhiteAxisRecorderPlugin : IAEPlugin
    {
        private readonly WhiteAxisRecorderService _recorder = new();

        public PluginSetting BuildPlugin()
        {
            return new PluginSetting
            {
                Name = "\u767d\u8f74\u8bb0\u5f55\u5668",
                LimitLevel = VIPLevel.Normal,
            };
        }

        public void OnLoad(AssemblyLoadContext loadContext)
        {
            _recorder.OnLoad();
        }

        public void Dispose()
        {
            _recorder.Dispose();
        }

        public void Update()
        {
            _recorder.Update();
        }

        public void OnPluginUI()
        {
            _recorder.Draw();
        }
    }
}
