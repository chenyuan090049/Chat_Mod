using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using UnityEngine.Networking;
using UnityEngine;

namespace AIChat.Services
{
    public static class LLMClient
    {
        [DataContract]
        public class ChatCompletionMessage
        {
            [DataMember(Name = "role")]
            public string role;

            [DataMember(Name = "content")]
            public string content;

            public ChatCompletionMessage()
            {
            }

            public ChatCompletionMessage(string role, string content)
            {
                this.role = role;
                this.content = content;
            }
        }

        [DataContract]
        private class ChatCompletionRequest
        {
            [DataMember(Name = "model")]
            public string model;

            [DataMember(Name = "messages")]
            public ChatCompletionMessage[] messages;
        }

        [DataContract]
        private class ChatCompletionResponse
        {
            [DataMember(Name = "choices")]
            public ChatCompletionChoice[] choices = null;

            [DataMember(Name = "error")]
            public ChatCompletionError error = null;
        }

        [DataContract]
        private class ChatCompletionChoice
        {
            [DataMember(Name = "message")]
            public ChatCompletionMessage message = null;
        }

        [DataContract]
        private class ChatCompletionError
        {
            [DataMember(Name = "message")]
            public string message = null;

            [DataMember(Name = "type")]
            public string type = null;

            [DataMember(Name = "code")]
            public string code = null;
        }

        public static string BuildChatPayload(string model, List<ChatCompletionMessage> messages)
        {
            var request = new ChatCompletionRequest
            {
                model = model,
                messages = (messages ?? new List<ChatCompletionMessage>()).ToArray()
            };
            return SerializeJson(request);
        }

        // 专为 DeepSeek/OpenAI 接口提供的方法
        public static IEnumerator SendDeepSeekRequest(string apiUrl, string apiKey, string jsonPayload, Action<string> onSuccess, Action<string, long> onFailure)
        {
            using (UnityWebRequest request = new UnityWebRequest(apiUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                // DeepSeek 标准鉴权头
                request.SetRequestHeader("Authorization", "Bearer " + apiKey);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string rawResponse = request.downloadHandler.text;
                    string extractedText = ParseDeepSeekResponse(rawResponse);

                    if (!string.IsNullOrEmpty(extractedText))
                    {
                        onSuccess(extractedText);
                    }
                    else
                    {
                        onFailure("无法从响应中解析出内容（JSON 结构不符）。原始返回: " + rawResponse, 200);
                    }
                }
                else
                {
                    string errorMsg = request.error;
                    // 尝试提取 DeepSeek 返回的详细报错体
                    if (request.downloadHandler != null && !string.IsNullOrEmpty(request.downloadHandler.text))
                    {
                        errorMsg += "\nDetails: " + request.downloadHandler.text;
                    }
                    onFailure(errorMsg, request.responseCode);
                }
            }
        }

        private static string ParseDeepSeekResponse(string rawJson)
        {
            try
            {
                var response = DeserializeJson<ChatCompletionResponse>(rawJson);
                if (response?.choices != null && response.choices.Length > 0)
                {
                    return response.choices[0]?.message?.content ?? string.Empty;
                }
                if (response?.error != null && !string.IsNullOrEmpty(response.error.message))
                {
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("解析 AI 响应时出错: " + ex.Message);
            }
            return string.Empty;
        }

        private static string SerializeJson<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T DeserializeJson<T>(string rawJson)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawJson)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
    }
}
