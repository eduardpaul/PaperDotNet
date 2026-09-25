from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class Counters(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The itemsAdded property
    items_added: Optional[int] = None
    # The pendingApprovals property
    pending_approvals: Optional[int] = None
    # The reminderRuns property
    reminder_runs: Optional[int] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> Counters:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: Counters
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return Counters()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "itemsAdded": lambda n : setattr(self, 'items_added', n.get_int_value()),
            "pendingApprovals": lambda n : setattr(self, 'pending_approvals', n.get_int_value()),
            "reminderRuns": lambda n : setattr(self, 'reminder_runs', n.get_int_value()),
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
        writer.write_int_value("itemsAdded", self.items_added)
        writer.write_int_value("pendingApprovals", self.pending_approvals)
        writer.write_int_value("reminderRuns", self.reminder_runs)
        writer.write_additional_data_value(self.additional_data)
    

