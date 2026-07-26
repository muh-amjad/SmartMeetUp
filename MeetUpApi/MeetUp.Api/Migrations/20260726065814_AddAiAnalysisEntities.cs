using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetUp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAnalysisEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalysisProviderRequested",
                table: "Meetings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnalysisProviderUsed",
                table: "Meetings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredAnalysisProviderKey",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AssigneeUserId = table.Column<string>(type: "text", nullable: true),
                    AssigneeNameRaw = table.Column<string>(type: "text", nullable: true),
                    DueDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceUtteranceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionItems_AspNetUsers_AssigneeUserId",
                        column: x => x.AssigneeUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ActionItems_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionItems_TranscriptUtterances_SourceUtteranceId",
                        column: x => x.SourceUtteranceId,
                        principalTable: "TranscriptUtterances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SourceUtteranceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Decisions_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Decisions_TranscriptUtterances_SourceUtteranceId",
                        column: x => x.SourceUtteranceId,
                        principalTable: "TranscriptUtterances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FollowUpEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EditedByUserId = table.Column<string>(type: "text", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUpEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FollowUpEmails_AspNetUsers_EditedByUserId",
                        column: x => x.EditedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FollowUpEmails_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MeetingSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OverviewText = table.Column<string>(type: "text", nullable: false),
                    KeyTopics = table.Column<List<string>>(type: "jsonb", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ModelUsed = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingSummaries_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_AssigneeUserId_Status",
                table: "ActionItems",
                columns: new[] { "AssigneeUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_MeetingId_Status",
                table: "ActionItems",
                columns: new[] { "MeetingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_SourceUtteranceId",
                table: "ActionItems",
                column: "SourceUtteranceId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_MeetingId",
                table: "Decisions",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_SourceUtteranceId",
                table: "Decisions",
                column: "SourceUtteranceId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpEmails_EditedByUserId",
                table: "FollowUpEmails",
                column: "EditedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpEmails_MeetingId",
                table: "FollowUpEmails",
                column: "MeetingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingSummaries_MeetingId",
                table: "MeetingSummaries",
                column: "MeetingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionItems");

            migrationBuilder.DropTable(
                name: "Decisions");

            migrationBuilder.DropTable(
                name: "FollowUpEmails");

            migrationBuilder.DropTable(
                name: "MeetingSummaries");

            migrationBuilder.DropColumn(
                name: "AnalysisProviderRequested",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "AnalysisProviderUsed",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "PreferredAnalysisProviderKey",
                table: "AspNetUsers");
        }
    }
}
