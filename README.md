# README.md

# SharePoint-Confluence Middleware Sync Engine

This project implements a middleware integration platform designed to provide **bi-directional, near real-time synchronization** between:

- **Microsoft SharePoint Lists (via Microsoft Graph API)**
- **Atlassian Confluence Databases (native lists, via Confluence REST API)**
- **Microsoft Teams Alerts (via MS Graph)**

The system is architected for long-term extensibility to integrate other SaaS platforms such as AutoTask PSA, Dynamics 365, and additional ITSM platforms.

## Project Goals

- ✅ Demonstrate proof-of-concept middleware integration for Support Operations
- ✅ Enable bi-directional sync between SharePoint Lists and Confluence Databases
- ✅ Provide alerting workflows via Microsoft Teams using MS Graph
- ✅ Build a modular integration platform for enterprise-grade expansion
- ✅ Architect clean API authentication models using scoped tokens

## Current Stack

- **.NET 8 Worker Service with Kestrel-exposed API Endpoints**
- **Azure App Service (target deployment)**
- **Azure Functions (future serverless expansion)**
- **Microsoft Graph API (SharePoint & Teams)**
- **Atlassian Confluence Cloud REST API v2 with Scoped API Tokens**
- **GitHub (source repository management)**

---