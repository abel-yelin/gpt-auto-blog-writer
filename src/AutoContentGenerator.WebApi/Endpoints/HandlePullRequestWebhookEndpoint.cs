﻿using AutoContentGenerator.WebApi.Models;
using AutoContentGenerator.WebApi.Services;
using LibGit2Sharp;
using Newtonsoft.Json.Linq;
using Octokit;

namespace AutoContentGenerator.WebApi.Endpoints;

public static class HandlePullRequestWebhookEndpoint
{
    public static async Task<IResult> HandleWebhook(HttpRequest request, AppConfig appConfig)
    {
        if (appConfig?.GitHubConfig == null || appConfig.OpenAIConfig == null)
        {
            return TypedResults.BadRequest("GitHub or OpenAI configuration is missing.");
        }

        if (appConfig.GitHubConfig.GitHubToken.StartsWith("<<") || appConfig.OpenAIConfig.OpenAIApiKey.StartsWith("<<"))
        {
            return TypedResults.BadRequest("GitHub or OpenAI configuration is missing.");
        }

        // Read GitHub configuration from environment variables
        string gitHubToken = appConfig.GitHubConfig.GitHubToken;
        string repoOwner = appConfig.GitHubConfig.GitHubRepoOwner;
        string repoName = appConfig.GitHubConfig.GitHubRepoName;
        string gitHubUser = appConfig.GitHubConfig.GitHubEmail;
        string postsDirectory = appConfig.GitHubConfig.GitHubPostsDirectory;

        // Parse the request payload
        string requestBody = await new StreamReader(request.Body).ReadToEndAsync();
        dynamic data = JObject.Parse(requestBody);
        if (data.action != "submitted")
        {
            return TypedResults.NoContent();
        }

        int pullRequestNumber = data.pull_request.number;
        string branchName = data.pull_request.head.@ref;

        var gitHubClient = new GitHubClient(new ProductHeaderValue("AutoContentGenerator"))
        {
            Credentials = new Octokit.Credentials(gitHubToken)
        };

        var reviewComments = await gitHubClient.PullRequest.ReviewComment.GetAll(repoOwner, repoName, pullRequestNumber);
        var commentsWithContext = new List<(string Path, int Line, string Comment)>();
        foreach (var comment in reviewComments)
        {
            string filePath = comment.Path;
            int line = comment.Position.HasValue ? comment.Position.Value : -1;
            string commentText = comment.Body;

            commentsWithContext.Add((filePath, line, commentText));
        }
        string overallReviewComment = data.review.body;

        // Get the list of changed files in the pull request
        var changedFiles = await gitHubClient.PullRequest.Files(repoOwner, repoName, pullRequestNumber);
        var changedMarkdownFiles = changedFiles.Where(file => file.FileName.EndsWith(".md")).ToList();

        // Assuming that only one markdown file was changed in the pull request, get the file name
        if (changedMarkdownFiles.Count == 0)
        {
            return TypedResults.NoContent();
        }

        string markdownFileName = changedMarkdownFiles[0].FileName;

        // Clone the repository and check out the appropriate branch
        string repoUrl = $"https://github.com/{repoOwner}/{repoName}.git";
        string clonePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var libgit2sharpCredentials = new UsernamePasswordCredentials { Username = gitHubToken, Password = gitHubToken };
        LibGit2Sharp.Repository.Clone(repoUrl, clonePath, new CloneOptions { CredentialsProvider = (_, __, ___) => libgit2sharpCredentials, BranchName = null });

        using (var repo = new LibGit2Sharp.Repository(clonePath))
        {
            string remoteBranchName = $"refs/remotes/origin/{branchName}";

            LibGit2Sharp.Branch remoteBranch = repo.Branches[remoteBranchName];
            LibGit2Sharp.Branch localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
            Commands.Checkout(repo, localBranch);

            // Edit the markdown file based on the comment
            string markdownFilePath = Path.Combine(clonePath, markdownFileName);
            string markdownContent = File.ReadAllText(markdownFilePath);
            EditContentResult editContentResult = await EditContentBasedOnComments(appConfig.OpenAIConfig, markdownContent, commentsWithContext, overallReviewComment); // Update this with the modified content based on the comment

            // Write the updated content back to the file
            File.WriteAllText(markdownFilePath, editContentResult.Content);

            // Commit the changes
            Commands.Stage(repo, markdownFilePath);
            var author = new LibGit2Sharp.Signature("GPT-Blog-Writer", "calumjs@live.com", DateTimeOffset.Now);
            repo.Commit(editContentResult.Comment, author, author);

            // Push the changes back to the repository
            var remote = repo.Network.Remotes["origin"];
            var refSpecs = new List<string> { $"+refs/heads/{branchName}:refs/remotes/origin/{branchName}" };
            repo.Network.Fetch(remote.Name, refSpecs, new FetchOptions { CredentialsProvider = (_, __, ___) => libgit2sharpCredentials });

            var options = new PushOptions { CredentialsProvider = (_, __, ___) => libgit2sharpCredentials };
            repo.Network.Push(remote, $"refs/heads/{branchName}:refs/heads/{branchName}", options);

            // Post a comment reply to the PR
            string commentReply = editContentResult.Comment;
            await PostCommentReply(gitHubClient, repoOwner, repoName, pullRequestNumber, commentReply);
        }

        return TypedResults.NoContent();
    }

    private async static Task<EditContentResult> EditContentBasedOnComments(OpenAIConfig openAiConfig, string markdownContent, List<(string Path, int Line, string Comment)> commentsWithContext, string overallReviewComment)
    {
        string apiKey = openAiConfig.OpenAIApiKey;
        string prompt = $$"""
You are a blog post editor for my blog on tea called Tea Treasury, at teatreasury.com
This is a blog all about tea - we cover all aspects essential and tangential related to tea, tea production, tea consumption, etc.
Feel free to be controversial in order to drive engagement.
Use markdown when you create the page.
Do not put the title in an h1 tag at the start of the article, because it will be added separately via my blog page.
Use an occaisional pun or thoughtful personal remark in the introduction or conclusion. Encourage people to engage with the discussion area under the post via various means.
Today's date is {{DateTime.Now:yyyy-MM-dd}}.
Include frontmatter on your page in the following format:
---
title: "<title>"
date: "{{DateTime.Now:yyyy-MM-dd}}"
author: "Tea Treasury"
tags:
- "<tag1>"
- "<tag2>"
- "<tag3>"
---
You will receive the blog post to be edited, along with comments from me about things that need to be fixed. You must only change the things which are explicitly requested. Respond ONLY with the edited blog post, do not give any extra comment. Make sure you give the entire blog post including unedited content because I will replace the entire file content with what you give me.
""";

        // Build the chat request content
        var chatContent = new JObject
        {
            ["model"] = openAiConfig.Model,
            ["messages"] = new JArray
        {
            new JObject { ["role"] = "system", ["content"] = prompt },
            new JObject { ["role"] = "user", ["content"] = markdownContent }
        }
        };

        foreach (var comment in commentsWithContext)
        {
            chatContent["messages"].Last.AddAfterSelf(new JObject { ["role"] = "user", ["content"] = $"Comment on line {comment.Line}: {comment.Comment}" });
        }

        chatContent["messages"].Last.AddAfterSelf(new JObject { ["role"] = "user", ["content"] = overallReviewComment });

        // Call the SendChatRequest method
        string editedContent = await OpenAIService.SendChatRequest(apiKey, chatContent.ToString());

        // Append a new user input for the comment explaining the changes
        chatContent["messages"].Last.AddAfterSelf(new JObject { ["role"] = "user", ["content"] = "Now give a comment explaining the changes you made in detail, with the reason why you made any artistic choices - reply with only the comment, no additional message" });

        // Call the SendChatRequest method again
        string commentResponse = await OpenAIService.SendChatRequest(apiKey, chatContent.ToString());

        return new EditContentResult
        {
            Content = editedContent,
            Comment = commentResponse
        };
    }

    private static async Task PostCommentReply(GitHubClient gitHubClient, string repoOwner, string repoName, int pullRequestNumber, string comment)
    {
        var pr = await gitHubClient.PullRequest.Get(repoOwner, repoName, pullRequestNumber);
        int issueNumber = pr.Number;
        await gitHubClient.Issue.Comment.Create(repoOwner, repoName, issueNumber, comment);
    }
}

public class EditContentResult
{
    public string Content { get; set; }
    public string Comment { get; set; }
}
