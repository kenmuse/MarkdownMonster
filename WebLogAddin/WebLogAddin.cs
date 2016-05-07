﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FontAwesome.WPF;
using HtmlAgilityPack;
using JoeBlogs;
using MarkdownMonster;
using MarkdownMonster.AddIns;
using WebLogAddin.Annotations;
using Westwind.Utilities;

namespace WebLogAddin
{
    public class WebLogAddin :  MarkdownMonsterAddin, IMarkdownMonsterAddin
    {
        private Post ActivePost { get; set; } = new Post();

        public override void OnApplicationStart()
        {
            base.OnApplicationStart();

            var menuItem = new AddInMenuItem()
            {
                Caption = "Weblog Publishing",
                EditorCommand = "weblog",
                FontawesomeIcon = FontAwesomeIcon.Wordpress
            };
            menuItem.Execute = new Action<object>(WebLogAddin_Execute);

            this.MenuItems.Add(menuItem);
        }

        public void WebLogAddin_Execute(object sender)
        {
            var form = new WebLogStart()
            {
                Owner = Model.Window
            };
            form.Model.AppModel = Model;
            form.Model.Addin = this;                       
            form.Show();                       
        }


        public bool SendPost()
        {
            var editor = Model.ActiveEditor;
            var doc = editor.MarkdownDocument;


            ActivePost = new Post()
            {
                DateCreated = DateTime.Now
            };

            // start by retrieving the current Markdown from the editor
            string markdown = editor.GetMarkdown();

            // Retrieve Meta data from post and clean up the raw markdown
            // so we render without the config data
            var meta  = GetPostConfigFromMarkdown(markdown);
           
            string html = doc.RenderHtml(meta.MarkdownBody);
            
            var config = WeblogApp.Configuration;            

            WeblogInfo weblogInfo;

            if (string.IsNullOrEmpty(meta.WeblogName) || !config.WebLogs.TryGetValue(meta.WeblogName, out weblogInfo))
            {
                MessageBox.Show("Invalid Weblog configuration selected.", "Weblog Posting Failed");
                return false;
            }

            var wrapper = new MetaWeblogWrapper(weblogInfo.ApiUrl,
                weblogInfo.Username,
                weblogInfo.Password);

            ActivePost.Body = SendImages(html, doc.Filename, wrapper);

            if (ActivePost.PostID > 0)
                wrapper.EditPost(ActivePost, true);
            else
            {
                ActivePost.PostID = wrapper.NewPost(ActivePost, true);
                
                // retrieve the raw editor markdown
                markdown = editor.GetMarkdown();

                // Update the Post Id into the Markdown
                if (!markdown.Contains("</postid>"))
                {
                    markdown = AddPostId(markdown, ActivePost.PostID);
                    editor.SetMarkdown(markdown);
                }
            }
            if (!string.IsNullOrEmpty(weblogInfo.PreviewUrl))
            {
                var url = weblogInfo.PreviewUrl.Replace("{0}", ActivePost.PostID.ToString());
                ShellUtils.GoUrl(url);
            }

            return true;
        }

        /// <summary>
        /// Adds a post id to Weblog configuration in a weblog post document.
        /// Only works if [categories] key exists.
        /// </summary>
        /// <param name="markdown"></param>
        /// <param name="postId"></param>
        /// <returns></returns>
        public string AddPostId(string markdown, int postId)
        {
            markdown = markdown.Replace("</categories>",
                    "</categories>\r\n" +
                    "<postid>" + ActivePost.PostID + "</postid>");

            return markdown;
        }

        public string NewWeblogPost(WeblogPostMetadata meta)
        {
            if (meta == null)
            {
                meta = new WeblogPostMetadata()
                {
                    Title = "Post Title",
                };
            }

            if (string.IsNullOrEmpty(meta.WeblogName))
                meta.WeblogName = "Name of registered blog to post to";
            
            return
$@"# {meta.Title}



<!-- Post Configuration -->
---
```xml
<abstract>
</abstract>
<categories>
</categories>
<keywords>
</keywords>
<weblog>
{meta.WeblogName}
</weblog>
```
<!-- End Post Configuration -->
";            
        }



        /// <summary>
        /// Parses each of the images in the document and posts them to the server.
        /// Updates the HTML with the returned Image Urls
        /// </summary>
        /// <param name="html"></param>
        /// <param name="filename"></param>
        /// <param name="wrapper"></param>
        /// <returns>update HTML string for the document with updated images</returns>
        private string SendImages(string html, string filename, MetaWeblogWrapper wrapper)
        {
            var basePath = Path.GetDirectoryName(filename);
            var baseName = Path.GetFileName(basePath);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // send up normalized path images as separate media items
            var images = doc.DocumentNode.SelectNodes("//img");
            if (images != null)
            {
                foreach (HtmlNode img in images)
                {
                    string imgFile = img.Attributes["src"]?.Value as string;
                    if (imgFile == null)
                        continue;

                    if (!imgFile.StartsWith("http"))
                    {
                        imgFile = Path.Combine(basePath, imgFile.Replace("/", "\\"));
                        if (System.IO.File.Exists(imgFile))
                        {
                            var media = new MediaObject()
                            {
                                Type = "application/image",
                                Bits = System.IO.File.ReadAllBytes(imgFile),
                                Name = baseName + "/" + Path.GetFileName(imgFile)
                            };
                            var mediaResult = wrapper.NewMediaObject(media);
                            img.Attributes["src"].Value = mediaResult.URL;
                            ;
                        }
                    }
                }
                
                html = doc.DocumentNode.OuterHtml;
            }

            return html;
        }
        

        /// <summary>
        /// Strips the Markdown Meta data from the message and populates
        /// the post structure with the meta data values.
        /// </summary>
        /// <param name="markdown"></param>        
        /// <returns></returns>
        public WeblogPostMetadata GetPostConfigFromMarkdown(string markdown)
        {
            var meta = new WeblogPostMetadata()
            {
                RawMarkdownBody = markdown,
                MarkdownBody = markdown
            };


            string config = StringUtils.ExtractString(markdown,
                "<!-- Post Configuration -->",
                "<!-- End Post Configuration -->",
                caseSensitive: false, allowMissingEndDelimiter: true, returnDelimiters: true);
            if (string.IsNullOrEmpty(config))
                return meta;

            // strip the config section
            meta.MarkdownBody = meta.MarkdownBody.Replace(config, "");


            // check for title in first line and remove it 
            // since the body shouldn't render the title
            var lines = StringUtils.GetLines(markdown);
            if (lines.Length > 0 && lines[0].Trim().StartsWith("# "))
            {
                meta.MarkdownBody = meta.MarkdownBody.Replace(lines[0], "").Trim();
                meta.Title = lines[0].Trim().Replace("# ", "");
            }

            
            if (string.IsNullOrEmpty(meta.Title))
                meta.Title = StringUtils.ExtractString(config, "\n<title>", "\n</title>").Trim();
            meta.Abstract = StringUtils.ExtractString(config, "\n<abstract>", "\n</abstract>").Trim();
            meta.Keywords = StringUtils.ExtractString(config, "\n<keywords>", "\n</keywords>").Trim();
            meta.Categories = StringUtils.ExtractString(config, "\n<categories>", "\n</categories>").Trim();
            meta.PostId = StringUtils.ExtractString(config, "\n<postid>", "</postid>").Trim();
            meta.WeblogName = StringUtils.ExtractString(config, "\n<weblog>", "</weblog>").Trim();

            ActivePost.Title = meta.Title;
            ActivePost.PostID = StringUtils.ParseInt(meta.PostId, 0);            
            ActivePost.Categories = meta.Categories.Split(new [] { ','},StringSplitOptions.RemoveEmptyEntries);

            ActivePost.mt_excerpt = meta.Abstract;
            ActivePost.mt_keywords = meta.Keywords;
    
            return meta;
        }

        /// <summary>
        /// This method sets the RawMarkdownBody
        /// </summary>
        /// <param name="meta"></param>
        /// <returns>Updated Markdown - also sets the RawMarkdownBody and MarkdownBody</returns>
        public string SetConfigInMarkdown(WeblogPostMetadata meta)
        {
            string markdown = meta.RawMarkdownBody;

            string origConfig = StringUtils.ExtractString(markdown, " <!-- Post Configuration -->", "!@#!-1", true, true);
            string newConfig = $@"<!-- Post Configuration -->
---
```xml
<abstract>
{meta.Abstract}
</abstract>
<categories>
{meta.Categories}
</categories>
<keywords>
{meta.Keywords}
</keywords>
<weblog>
{meta.WeblogName}
</weblog>
```
<!-- End Post Configuration -->
";

            if (string.IsNullOrEmpty(origConfig))
            {
                markdown += "\r\n" + newConfig;
            }
            else
                markdown = markdown.Replace(origConfig, newConfig);

            meta.RawMarkdownBody = markdown;
            meta.MarkdownBody = meta.RawMarkdownBody.Replace(newConfig, "");

            return markdown;
        }
    }

    public class WeblogPostMetadata : INotifyPropertyChanged
    {
        private string _title;
        private string _abstract;
        public string PostId { get; set; }

        public string Title
        {
            get { return _title; }
            set
            {
                if (value == _title) return;
                _title = value;
                OnPropertyChanged(nameof(Title));
            }
        }

        /// <summary>
        /// This should hold the sanitized markdown text
        /// stripped of the config data.
        /// </summary>
        public string MarkdownBody { get; set; }

        /// <summary>
        /// This should hold the raw markdown text retrieved
        /// from the editor which will contain the meta post data
        /// </summary>
        public string RawMarkdownBody { get; set; }

        public string Abstract
        {
            get { return _abstract; }
            set
            {
                if (value == _abstract) return;
                _abstract = value;
                OnPropertyChanged(nameof(Abstract));
            }
        }

        public string Keywords { get; set; }
        public string Categories { get; set; }

        public string WeblogName { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}
