from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class CalendarEntry(AdditionalDataHolder, Parsable):
    """
    An event occurrence or a due task in a time range (CAL-03).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The allDay property
    all_day: Optional[bool] = None
    # The attendees property
    attendees: Optional[list[UUID]] = None
    # The createdBy property
    created_by: Optional[UUID] = None
    # The end property
    end: Optional[datetime.datetime] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The kind property
    kind: Optional[str] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The listName property
    list_name: Optional[str] = None
    # The location property
    location: Optional[str] = None
    # The masterItemId property
    master_item_id: Optional[UUID] = None
    # The occurrenceStart property
    occurrence_start: Optional[datetime.datetime] = None
    # The recurring property
    recurring: Optional[bool] = None
    # Minutes before the start to remind attendees (CAL-01, NTF-02).
    reminder_minutes: Optional[int] = None
    # The start property
    start: Optional[datetime.datetime] = None
    # The status property
    status: Optional[str] = None
    # The title property
    title: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CalendarEntry:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CalendarEntry
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CalendarEntry()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "allDay": lambda n : setattr(self, 'all_day', n.get_bool_value()),
            "attendees": lambda n : setattr(self, 'attendees', n.get_collection_of_primitive_values(UUID)),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_uuid_value()),
            "end": lambda n : setattr(self, 'end', n.get_datetime_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_str_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "listName": lambda n : setattr(self, 'list_name', n.get_str_value()),
            "location": lambda n : setattr(self, 'location', n.get_str_value()),
            "masterItemId": lambda n : setattr(self, 'master_item_id', n.get_uuid_value()),
            "occurrenceStart": lambda n : setattr(self, 'occurrence_start', n.get_datetime_value()),
            "recurring": lambda n : setattr(self, 'recurring', n.get_bool_value()),
            "reminderMinutes": lambda n : setattr(self, 'reminder_minutes', n.get_int_value()),
            "start": lambda n : setattr(self, 'start', n.get_datetime_value()),
            "status": lambda n : setattr(self, 'status', n.get_str_value()),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_uuid_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("allDay", self.all_day)
        writer.write_collection_of_primitive_values("attendees", self.attendees)
        writer.write_uuid_value("createdBy", self.created_by)
        writer.write_datetime_value("end", self.end)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_str_value("kind", self.kind)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_str_value("listName", self.list_name)
        writer.write_str_value("location", self.location)
        writer.write_uuid_value("masterItemId", self.master_item_id)
        writer.write_datetime_value("occurrenceStart", self.occurrence_start)
        writer.write_bool_value("recurring", self.recurring)
        writer.write_int_value("reminderMinutes", self.reminder_minutes)
        writer.write_datetime_value("start", self.start)
        writer.write_str_value("status", self.status)
        writer.write_str_value("title", self.title)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

