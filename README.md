# Confluence Sync Service / SharePoint-Confluence Middleware Sync Engine

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL%20v3-blue.svg)](LICENSE)

This project implements a middleware integration platform designed to provide **bi-directional, near real-time synchronization** between:

- **Microsoft SharePoint Lists (via Microsoft Graph API)**
- **Atlassian Confluence Databases (native lists, via Confluence REST API)**
- **Microsoft Teams Alerts (via MS Graph)**

The system is architected for long-term extensibility to integrate other SaaS platforms such as AutoTask PSA, Dynamics 365, and additional ITSM platforms.

---

## Project Goals

- ✅ Demonstrate proof-of-concept middleware integration for Support Operations  
- ✅ Enable bi-directional sync between SharePoint Lists and Confluence Databases  
- ✅ Provide alerting workflows via Microsoft Teams using MS Graph  
- ✅ Build a modular integration platform for enterprise-grade expansion  
- ✅ Architect clean API authentication models using scoped tokens  

---

## Current Stack

- **.NET 8 Worker Service with Kestrel-exposed API Endpoints**  
- **Azure App Service (target deployment)**  
- **Azure Functions (future serverless expansion)**  
- **Microsoft Graph API (SharePoint & Teams)**  
- **Atlassian Confluence Cloud REST API v2 with Scoped API Tokens**  
- **GitHub (source repository management)**  

---

## Documentation

- **[Workflow Architecture](./docs/workflow_architecture.md)**
- **[Sync Architecture](./docs/sync_architecture.md)** – Detailed technical documentation of the bidirectional sync system, including field mapping, change detection, and performance characteristics
- **[Notifications & ACK Architecture](./docs/NOTIFICATIONS_ACK_ARCHITECTURE.md)**
- **API Documentation** – *(Coming Soon)*  
- **Deployment Guide** – *(Coming Soon)*  

---

## Start Here: Teams Notifications & ACK

- **What it does:** Sends initial Teams notifications when a task group becomes eligible, then posts chasers as threaded replies if tasks go overdue. Users click a signed **Mark Complete** link to acknowledge.
- **How to wire it:** Configure your public base URL, Teams team/channel ids, and HMAC secret (Key Vault recommended). Then run the worker—initial messages appear automatically when the first eligible group is detected.
- **Learn more:** See [Notifications & ACK Architecture](./docs/NOTIFICATIONS_ACK_ARCHITECTURE.md).

---

## License

This project is licensed under the terms of the [GNU Affero General Public License v3.0 (AGPL-3.0)](LICENSE).  
You may copy, modify, and distribute this program under the conditions laid out in the license, which ensures that modified versions made available over a network must also be shared under the same license.
