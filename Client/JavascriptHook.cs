﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTANetworkShared;
using Microsoft.ClearScript;
using Microsoft.ClearScript.Windows;
using NativeUI;
using Vector3 = GTANetworkShared.Vector3;

namespace GTANetwork
{
    public class ClientsideScriptWrapper
    {
        public ClientsideScriptWrapper(JScriptEngine en, string rs)
        {
            Engine = en;
            ResourceParent = rs;
        }

        public JScriptEngine Engine { get; set; }
        public string ResourceParent { get; set; }
    }

    public class JavascriptHook : Script
    {
        public JavascriptHook()
        {
            Tick += OnTick;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            ScriptEngines = new List<ClientsideScriptWrapper>();
            ThreadJumper = new List<Action>();
        }

        public static List<ClientsideScriptWrapper> ScriptEngines;

        public static List<Action> ThreadJumper;

        public static void InvokeServerEvent(string eventName, object[] arguments)
        {
            ThreadJumper.Add(() =>
            {
                lock (ScriptEngines) ScriptEngines.ForEach(en => en.Engine.Script.API.invokeServerEvent(eventName, arguments));
            });
        }

        public static void InvokeMessageEvent(string msg)
        {
            if (msg == null) return;
            ThreadJumper.Add(() =>
            {
                if (msg.StartsWith("/"))
                {
                    lock (ScriptEngines)
                    {
                        ScriptEngines.ForEach(en => en.Engine.Script.API.invokeChatCommand(msg));
                    }
                }
                else
                {
                    lock (ScriptEngines)
                    {
                        ScriptEngines.ForEach(en => en.Engine.Script.API.invokeChatMessage(msg));
                    }
                }
            });
        }

        public void OnTick(object sender, EventArgs e)
        {
            if (ThreadJumper.Count > 0)
            {
                ThreadJumper.ForEach(a =>
                {
                    try
                    {
                        a.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }
                });
                ThreadJumper.Clear();
            }

            lock (ScriptEngines)
            {
                foreach (var engine in ScriptEngines)
                {
                    try
                    {
                        engine.Engine.Script.API.invokeUpdate();
                    }  
                    catch (ScriptEngineException ex)
                    {
                        LogException(ex);
                    }
                }
            }
        }

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            lock (ScriptEngines)
            {
                foreach (var engine in ScriptEngines)
                {
                    try
                    {
                        engine.Engine.Script.API.invokeKeyDown(sender, e);
                    }
                    catch (ScriptEngineException ex)
                    {
                        LogException(ex);
                    }
                }
            }
        }

        public void OnKeyUp(object sender, KeyEventArgs e)
        {
            lock (ScriptEngines)
            {
                foreach (var engine in ScriptEngines)
                {
                    try
                    {
                        engine.Engine.Script.API.invokeKeyUp(sender, e);
                    }
                    catch (ScriptEngineException ex)
                    {
                        LogException(ex);
                    }
                }
            }
        }

        public static void StartScript(ClientsideScript script)
        {
            ThreadJumper.Add((() =>
            {
                var scriptEngine = new JScriptEngine();
                scriptEngine.AddHostObject("host", new HostFunctions());
                scriptEngine.AddHostObject("API", new ScriptContext());
                scriptEngine.AddHostType("Enumerable", typeof(Enumerable));
                scriptEngine.AddHostType("List", typeof(IList));
                scriptEngine.AddHostType("KeyEventArgs", typeof(KeyEventArgs));
                scriptEngine.AddHostType("Keys", typeof(Keys));
                scriptEngine.AddHostType("Point", typeof(Point));
                scriptEngine.AddHostType("Size", typeof(Size));
                scriptEngine.AddHostType("Vector3", typeof(Vector3));
                

                try
                {
                    scriptEngine.Execute(script.Script);
                }
                catch (ScriptEngineException ex)
                {
                    LogException(ex);
                }
                finally
                {
                    scriptEngine.Script.API.invokeResourceStart();
                    lock (ScriptEngines) ScriptEngines.Add(new ClientsideScriptWrapper(scriptEngine, script.ResourceParent));
                }
            }));
        }

        public static void StopAllScripts()
        {
            ThreadJumper.Add(() =>
            {
                lock (ScriptEngines)
                {
                    foreach (var engine in ScriptEngines)
                    {
                        engine.Engine.Script.API.invokeResourceStop();
                        engine.Engine.Dispose();
                    }

                    ScriptEngines.Clear();
                }
            });
        }

        public static void StopScript(string resourceName)
        {
            ThreadJumper.Add(delegate
            {
                lock (ScriptEngines)
                    for (int i = ScriptEngines.Count - 1; i >= 0; i--)
                    {
                        if (ScriptEngines[i].ResourceParent != resourceName) continue;
                        ScriptEngines[i].Engine.Script.API.invokeResourceStop();
                        ScriptEngines[i].Engine.Dispose();
                        ScriptEngines.RemoveAt(i);
                    }
            });
        }

        private static void LogException(Exception ex)
        {
            Func<string, int, string[]> splitter = (string input, int everyN) =>
            {
                var list = new List<string>();
                for (int i = 0; i < input.Length; i += everyN)
                {
                    list.Add(input.Substring(i, Math.Min(everyN, input.Length - i)));
                }
                return list.ToArray();
            };

            Util.SafeNotify("~r~~h~Clientside Javascript Error~h~~w~");
            
            foreach (var s in splitter(ex.Message, 99))
            {
                Util.SafeNotify(s);
            }

            DownloadManager.Log(ex.ToString());
        }
    }

    public class ScriptContext
    {
        public enum ReturnType
        {
            Int = 0,
            UInt = 1,
            Long = 2,
            ULong = 3,
            String = 4,
            Vector3 = 5,
            Vector2 = 6,
            Float = 7
        }

        public LocalHandle NetToLocal(NetHandle handle)
        {
            return new LocalHandle(Main.NetEntityHandler.NetToEntity(handle.Value)?.Handle ?? 0);
        }

        public void callNative(string hash, params object[] args)
        {
            Hash ourHash;
            if (!Hash.TryParse(hash, out ourHash))
                return;
            Function.Call(ourHash, args.Select(o => new InputArgument(o)).ToArray());
        }

        public object returnNative(string hash, int returnType, params object[] args)
        {
            Hash ourHash;
            if (!Hash.TryParse(hash, out ourHash))
                return null;
            var fArgs = args.Select(o => new InputArgument(o)).ToArray();
            switch ((ReturnType)returnType)
            {
                case ReturnType.Int:
                    return Function.Call<int>(ourHash, fArgs);
                case ReturnType.UInt:
                    return Function.Call<uint>(ourHash, fArgs);
                case ReturnType.Long:
                    return Function.Call<long>(ourHash, fArgs);
                case ReturnType.ULong:
                    return Function.Call<ulong>(ourHash, fArgs);
                case ReturnType.String:
                    return Function.Call<string>(ourHash, fArgs);
                case ReturnType.Vector3:
                    return Function.Call<Vector3>(ourHash, fArgs);
                case ReturnType.Vector2:
                    return Function.Call<Vector2>(ourHash, fArgs);
                case ReturnType.Float:
                    return Function.Call<float>(ourHash, fArgs);
                default:
                    return null;
            }
        }
        
        public void playSoundFrontEnd(string audioLib, string audioName)
        {
            Function.Call((Hash)0x2F844A8B08D76685, audioLib, true);
            Function.Call((Hash)0x67C540AA08E4A6F5, -1, audioName, audioLib);
        }

        public void showShard(string text)
        {
            NativeUI.BigMessageThread.MessageInstance.ShowMissionPassedMessage(text);
        }

        public void showShard(string text, int timeout)
        {
            NativeUI.BigMessageThread.MessageInstance.ShowMissionPassedMessage(text, timeout);
        }

        public SizeF getScreenResolutionMantainRatio()
        {
            return UIMenu.GetScreenResolutionMantainRatio();
        }

        public void sendNotification(string text)
        {
            Util.SafeNotify(text);
        }

        public UIMenu createMenu(string banner, string subtitle, double x, double y, int anchor)
        {
            var offset = convertAnchorPos((float) x, (float) y, (Anchor) anchor);
            return new UIMenu(banner, subtitle, new Point((int)(offset.X), (int)(offset.Y)));
        }

        public UIMenu createMenu(string subtitle, double x, double y, int anchor)
        {
            var offset = convertAnchorPos((float)x, (float)y, (Anchor)anchor);
            var newM = new UIMenu("", subtitle, new Point((int)(offset.X), (int)(offset.Y)));
            newM.SetBannerType(new UIResRectangle());
            return newM;
        }

        public MenuPool getMenuPool()
        {
            return new MenuPool();
        }

        public void drawMenu(UIMenu menu)
        {
            menu.ProcessControl();
            menu.ProcessMouse();
            menu.Draw();
        }

        public LocalHandle createBlip(Vector3 pos)
        {
            var blip = World.CreateBlip(pos.ToVector());
            if (!Main.BlipCleanup.Contains(blip.Handle))
                Main.BlipCleanup.Add(blip.Handle);
            return new LocalHandle(blip.Handle);
        }

        public void setBlipPosition(LocalHandle blip, Vector3 pos)
        {
            if (new Blip(blip.Value).Exists())
            {
                new Blip(blip.Value).Position = pos.ToVector();
            }
        }

        public void removeBlip(LocalHandle blip)
        {
            if (new Blip(blip.Value).Exists())
            {
                new Blip(blip.Value).Remove();
            }
        }
        
        public void setBlipScale(LocalHandle blip, double scale)
        {
            setBlipScale(blip, (float) scale);
        }

        public void setBlipScale(LocalHandle blip, float scale)
        {
            if (new Blip(blip.Value).Exists())
            {
                new Blip(blip.Value).Scale = scale;
            }
        }

        public LocalHandle createMarker(int markerType, Vector3 pos, Vector3 dir, Vector3 rot, Vector3 scale, int r, int g, int b, int alpha)
        {
            return new LocalHandle(Main.NetEntityHandler.CreateLocalMarker(markerType, pos.ToVector(), dir.ToVector(), rot.ToVector(), scale.ToVector(), alpha, r, g, b));
        }

        public void deleteMarker(LocalHandle handle)
        {
            Main.NetEntityHandler.DeleteLocalMarker(handle.Value);
        }
        
        public string getResourceFilePath(string resourceName, string fileName)
        {
            return FileTransferId._DOWNLOADFOLDER_ + resourceName + "\\" + fileName;
        }
        
        public void dxDrawTexture(string path, Point pos, Size size)
        {
            Util.DxDrawTexture(path, path, pos.X, pos.Y, size.Width, size.Height, 0f, 255, 255, 255, 255);
        }

        public void drawGameTexture(string dict, string txtName, double x, double y, double width, double height, double heading,
            int r, int g, int b, int alpha)
        {
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, dict))
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, dict, true);

            int screenw = Game.ScreenResolution.Width;
            int screenh = Game.ScreenResolution.Height;
            const float hh = 1080f;
            float ratio = (float)screenw / screenh;
            var ww = hh * ratio;


            float w = (float)(width / ww);
            float h = (float)(height / hh);
            float xx = (float)(x / ww) + w * 0.5f;
            float yy = (float)(y / hh) + h * 0.5f;

            Function.Call(Hash.DRAW_SPRITE, dict, txtName, xx, yy, w, h, heading, r, g, b, alpha);
        }

        public void drawText(string caption, double xPos, double yPos, double scale, int r, int g, int b, int alpha, int font,
            int justify, bool shadow, bool outline, int wordWrap)
        {
            int screenw = Game.ScreenResolution.Width;
            int screenh = Game.ScreenResolution.Height;
            const float height = 1080f;
            float ratio = (float)screenw / screenh;
            var width = height * ratio;

            float x = (float)(xPos) / width;
            float y = (float)(yPos) / height;

            Function.Call(Hash.SET_TEXT_FONT, font);
            Function.Call(Hash.SET_TEXT_SCALE, 1.0f, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, alpha);
            if (shadow)
                Function.Call(Hash.SET_TEXT_DROP_SHADOW);
            if (outline)
                Function.Call(Hash.SET_TEXT_OUTLINE);
            switch (justify)
            {
                case 1:
                    Function.Call(Hash.SET_TEXT_CENTRE, true);
                    break;
                case 2:
                    Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
                    Function.Call(Hash.SET_TEXT_WRAP, 0, x);
                    break;
            }

            if (wordWrap != 0)
            {
                float xsize = (float)(xPos + wordWrap) / width;
                Function.Call(Hash.SET_TEXT_WRAP, x, xsize);
            }

            Function.Call(Hash._SET_TEXT_ENTRY, "jamyfafi");
            NativeUI.UIResText.AddLongString(caption);
            Function.Call(Hash._DRAW_TEXT, x, y);
        }

        public bool isPed(LocalHandle ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_A_PED, ent.Value);
        }

        public bool isVehicle(LocalHandle ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_A_VEHICLE, ent.Value);
        }

        public bool isProp(LocalHandle ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_AN_OBJECT, ent.Value);
        }

        public float toFloat(double d)
        {
            return (float) d;
        }

        public void wait(int ms)
        {
            Script.Wait(ms);
        }

        public void triggerServerEvent(string eventName, params object[] arguments)
        {
            Main.TriggerServerEvent(eventName, arguments);
        }

        public delegate void ServerEventTrigger(string eventName, object[] arguments);
        //public delegate void ChatEvent(string msg, CancelEventArgs cancelEv);
        public delegate void ChatEvent(string msg);

        public event EventHandler onResourceStart;
        public event EventHandler onResourceStop;
        public event EventHandler onUpdate;
        public event KeyEventHandler onKeyDown;
        public event KeyEventHandler onKeyUp;
        public event ServerEventTrigger onServerEventTrigger;
        public event ChatEvent onChatMessage;
        public event ChatEvent onChatCommand;

        internal void invokeChatMessage(string msg)
        {
            onChatMessage?.Invoke(msg);
        }

        internal void invokeChatCommand(string msg)
        {
            onChatCommand?.Invoke(msg);
        }

        internal void invokeServerEvent(string eventName, object[] arsg)
        {
            onServerEventTrigger?.Invoke(eventName, arsg);
        }

        internal void invokeResourceStart()
        {
            onResourceStart?.Invoke(this, EventArgs.Empty);
        }

        internal void invokeUpdate()
        {
            onUpdate?.Invoke(this, EventArgs.Empty);
        }

        internal void invokeResourceStop()
        {
            onResourceStop?.Invoke(this, EventArgs.Empty);
        }

        internal void invokeKeyDown(object sender, KeyEventArgs e)
        {
            onKeyDown?.Invoke(sender, e);
        }

        internal void invokeKeyUp(object sender, KeyEventArgs e)
        {
            onKeyUp?.Invoke(sender, e);
        }

        internal PointF convertAnchorPos(float x, float y, Anchor anchor)
        {
            var res = UIMenu.GetScreenResolutionMantainRatio();

            switch (anchor)
            {
                case Anchor.TopLeft:
                    return new PointF(x, y);
                case Anchor.TopCenter:
                    return new PointF(res.Width / 2 + x, 0 + y);
                case Anchor.TopRight:
                    return new PointF(res.Width - x, y);
                case Anchor.MiddleLeft:
                    return new PointF(x, res.Height / 2 + y);
                case Anchor.MiddleCenter:
                    return new PointF(res.Width / 2 + x, res.Height / 2 + y);
                case Anchor.MiddleRight:
                    return new PointF(res.Width - x, res.Height / 2 + y);
                case Anchor.BottomLeft:
                    return new PointF(x, res.Height - y);
                case Anchor.BottomCenter:
                    return new PointF(res.Width / 2 + x, res.Height - y);
                case Anchor.BottomRight:
                    return new PointF(res.Width - x, res.Height - y);
                default:
                    return PointF.Empty;
            }
        }

        internal enum Anchor
        {
            TopLeft = 0,
            TopCenter = 1,
            TopRight = 2,
            MiddleLeft = 3,
            MiddleCenter = 4,
            MiddleRight = 6,
            BottomLeft = 7,
            BottomCenter = 8,
            BottomRight = 9,
        }
    }

}