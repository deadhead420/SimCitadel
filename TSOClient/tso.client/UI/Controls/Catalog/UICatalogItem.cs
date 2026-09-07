using System;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.IO;
using Microsoft.Xna.Framework.Graphics;
using FSO.Client.Utils;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using FSO.Common.Rendering.Framework.Model;

namespace FSO.Client.UI.Controls.Catalog
{
    public class UICatalogItem : UIElement
    {
        public static TextStyle CountStyle = TextStyle.Create(Color.White, 11, true);
        
        public Texture2D Icon;
        private UITooltipHandler m_TooltipHandler;
        private bool Active;
        private bool Disabled;
        private bool Hovered;
        private Texture2D Background;
        private UIMouseEventRef ClickHandler;
        public event ButtonClickDelegate OnMouseEvent;
        public UICatalogElement Info;
        public int Index;

        public void SetActive(bool active)
        {
            this.Active = active;
            UpdateHighlight();
        }

        public UICatalogItem(bool Active)
        {
            SetActive(Active);
            m_TooltipHandler = UIUtils.GiveTooltip(this);
            ClickHandler = ListenForMouse(new Rectangle(0, 0, 45, 45), new UIMouseEvent(MouseEvt));
        }

        public void SetHover(bool hover)
        {
            this.Hovered = hover;
            UpdateHighlight();
        }

        public void SetDisabled(bool disable)
        {
            this.Disabled = disable;
            UpdateHighlight();
        }

        public void UpdateHighlight()
        {
            if (Disabled) Background = TextureGenerator.GetCatalogDisabled(GameFacade.GraphicsDevice);
            else if (Active || Hovered) Background = TextureGenerator.GetCatalogActive(GameFacade.GraphicsDevice);
            else Background = TextureGenerator.GetCatalogInactive(GameFacade.GraphicsDevice);

            Invalidate();
        }

        private void MouseEvt(UIMouseEventType type, UpdateState state)
        {
            if (type == UIMouseEventType.MouseDown && OnMouseEvent != null) OnMouseEvent(this); //pass to parents to handle
            if (type == UIMouseEventType.MouseOver) SetHover(true);
            if (type == UIMouseEventType.MouseOut) SetHover(false);
        }

        public override void Draw(UISpriteBatch batch)
        {
            if (!Visible) return;

	    if (Icon != null)
            {
                DrawLocalTexture(batch, Background, new Vector2(0, 0));

                if (Icon.Width / Icon.Height > 2)
                {
                    // Special multi-state button icon
                    DrawLocalTexture(batch, Icon, new Rectangle((!Disabled && (Active || Hovered)) ? Icon.Width / 4 : 0, 0, Icon.Width / 4, Icon.Height), new Vector2(2, 2));
                }
                else
                {
                    // Always draw single-frame object icons centered with proper scaling
                    float scale = 37.0f / Math.Max(Icon.Height, Icon.Width);
                    if (scale > 1.0f) scale = 1.0f; // Don't upscale tiny icons past 1:1

                    Vector2 offset = new Vector2(
                        2 + ((37 - Icon.Width * scale) / 2),
                        2 + ((37 - Icon.Height * scale) / 2)
                    );

                    DrawLocalTexture(batch, Icon, new Rectangle(0, 0, Icon.Width, Icon.Height), offset, new Vector2(scale, scale));
                }
            } else
            {
                DrawLocalTexture(batch, Background, new Vector2(0, 0));
            }

            if (Info.Count != null)
            {
                //draw the count on top of the icon
                DrawLocalString(batch, "x"+Info.Count.Value.ToString(), new Vector2(0, 22 + 9), CountStyle, new Rectangle(0, 0, 39, 1), TextAlignment.Right | TextAlignment.Middle);
            }
        }

        public override Rectangle GetBounds()
        {
            return new Rectangle(0, 0, ClickHandler.Region.Width, ClickHandler.Region.Height);
        }
    }
}
